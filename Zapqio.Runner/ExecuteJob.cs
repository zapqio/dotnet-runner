using Zapqio.Runner.Core;
using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner
{
    /// <summary>
    /// Wykonanie jednego przydziału. Wołane przez <see cref="JobScheduler"/> na osobnym wątku, dla
    /// kilku zadań naraz - nie trzyma żadnego stanu między wywołaniami, a wszystko, co idzie do
    /// platformy, przechodzi przez kolejkę wyjściową.
    /// </summary>
    public class ExecuteJob
    {
        private readonly WSClient _client;
        private readonly ScopedConsole _scopedConsole;
        private readonly MethodsProvider _methodsProvider;
        private readonly PendingJobReturns _pending;
        private readonly ILogger<ExecuteJob> _logger;

        public ExecuteJob(
            WSClient client,
            ScopedConsole scopedConsole,
            MethodsProvider methodsProvider,
            PendingJobReturns pending,
            ILogger<ExecuteJob> logger)
        {
            _client = client;
            _scopedConsole = scopedConsole;
            _methodsProvider = methodsProvider;
            _pending = pending;
            _logger = logger;
        }

        public async Task Exec(MessageJob message)
        {
            var method = _methodsProvider.GetMethod(message.Name);
            var version = _methodsProvider.VersionFor(method);
            bool status = true;
            string outData = null;
            if (method == null)
            {
                _client.SendLog(new MessageLog
                {
                    Date = DateTimeOffset.Now,
                    Level = MessageLogLevel.Error,
                    JobId = message.Id,
                    AttemptId = message.AttemptId,
                    Message = $"Not found method: {message.Name}"
                });
                status = false;
            }
            else
            {
                try
                {
                    // Log startowy PRZED metodą, i to faktycznie wysłany: to on przestawia zadanie po
                    // stronie platformy na Executing (skutek mógł nastąpić). Gdy nie wyszedł, zadanie
                    // zostaje w Dispatched (nic się nie wykonało) i platforma zwróci je do kolejki -
                    // uruchomienie metody mimo to skończyłoby się podwójnym wykonaniem.
                    var started = await _client.SendLogAndWaitAsync(new MessageLog
                    {
                        Date = DateTimeOffset.Now,
                        Level = MessageLogLevel.Info,
                        JobId = message.Id,
                        AttemptId = message.AttemptId,
                        ExecutionVersion = version,
                        Message = $"Run Job: {DateTimeOffset.Now:s}"
                    });
                    if (!started)
                    {
                        _logger.LogWarning(
                            "Log startowy zadania {JobId} (próba {AttemptId}) nie wyszedł - zadanie nie rusza, platforma zwróci je do kolejki",
                            message.Id, message.AttemptId);
                        return;
                    }

                    // Kontekst zadania dla modułu (JobContext.Current): id operacji, stałe między
                    // wysyłkami, po którym metoda z nieidempotentnym skutkiem rozpoznaje powtórkę.
                    // AsyncLocal przepływa do Task.Run, więc metoda widzi go także w swoich wątkach;
                    // po zakończeniu znika, żeby kolejne zadanie nie odziedziczyło cudzego kontekstu.
                    using (JobContext.Begin(new JobContext(message.Id, message.AttemptId, message.Name)))
                    {
                        outData = await Task.Run(() =>
                        {
                            using var consoleScope = _scopedConsole.BeginScope(message);
                            return method.Run(message.Data);
                        });
                    }
                }
                catch (Exception ex)
                {
                    _client.SendLog(new MessageLog
                    {
                        Date = DateTimeOffset.Now,
                        Level = MessageLogLevel.Error,
                        JobId = message.Id,
                        AttemptId = message.AttemptId,
                        Message = $"Main exception: {ex}"
                    });
                    status = false;
                }
            }

            var result = new MessageJobReturn
            {
                Id = message.Id,
                AttemptId = message.AttemptId,
                ExecutionVersion = version,
                Status = status ? MessageResponseStatus.OK : MessageResponseStatus.ERROR,
                Data = status ? outData : null
            };

            var sentSeq = await _client.SendJobReturn(result.Id, result.AttemptId, result.Status, result.Data, result.ExecutionVersion);
            if (sentSeq is { } seq)
            {
                // Poszło, ale bez potwierdzenia: zostaje do ponowienia, gdyby połączenie okazało się
                // martwe, zanim platforma da znak życia (patrz PendingJobReturns).
                _pending.MarkSent(result, seq);
                return;
            }

            // Wynik nie przepadł: host wyśle go po ponownym połączeniu z tym samym attemptId, a
            // platforma przyjmie, jeśli wciąż trzyma zadanie jako „wynik nieznany" (§5.5 protokołu).
            _pending.MarkFailed(result);
            _client.SendLog(new MessageLog
            {
                Date = DateTimeOffset.Now,
                Level = MessageLogLevel.Error,
                JobId = message.Id,
                AttemptId = message.AttemptId,
                Message = $"Failed to send JobReturn ({result.Status}) - the result is kept and will be resent after reconnecting"
            });
        }
    }
}
