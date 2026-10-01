using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Background
{
    /// <summary>
    /// Pętla połączenia: uzgodnienie, <c>Info</c>, ponowienie zaległych wyników, odczyt gniazda.
    /// Odebrany przydział trafia do <see cref="JobScheduler"/> i pętla natychmiast wraca do odczytu -
    /// gniazdo jest czytane zawsze, także gdy wszystkie sloty pracują. Zapisy idą przez
    /// <see cref="Outbox"/> i <see cref="OutboundSender"/>.
    /// </summary>
    public class RequestBindBackground : BackgroundService
    {
        private readonly WSClient _client;
        private readonly ILogger<RequestBindBackground> _logger;
        private readonly PendingJobReturns _pending;
        private readonly JobScheduler _scheduler;
        private readonly Outbox _outbox;
        private readonly OutboundSender _sender;
        private readonly AppSettings _settings;
        private readonly Deployments.DeploymentCoordinator? _deployments;
        private bool _runMethodFirstConnected = false;

        /// <summary>Ustawiane w <see cref="StopAsync"/>, zanim host anuluje pętlę - żeby po zamknięciu gniazda nie próbowała się łączyć na nowo.</summary>
        private volatile bool _stopping = false;

        /// <summary>Zwłoka po pierwszej nieudanej próbie; kolejne podwajają ją aż do <see cref="MaxReconnectDelay"/>.</summary>
        private static readonly TimeSpan BaseReconnectDelay = TimeSpan.FromSeconds(3);

        /// <summary>Sufit wycofywania się - wyżej runner przestałby zauważać, że Web wrócił.</summary>
        private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(60);

        private int _failedConnects;

        /// <summary>Zwłoka po pierwszym błędzie pętli; kolejne z rzędu podwajają ją do <see cref="MaxLoopErrorDelay"/>.</summary>
        private static readonly TimeSpan BaseLoopErrorDelay = TimeSpan.FromSeconds(1);

        private static readonly TimeSpan MaxLoopErrorDelay = TimeSpan.FromSeconds(30);

        private int _loopErrors;

        public RequestBindBackground(
            WSClient client,
            ILogger<RequestBindBackground> logger,
            PendingJobReturns pending,
            JobScheduler scheduler,
            Outbox outbox,
            OutboundSender sender,
            AppSettings settings, Deployments.DeploymentCoordinator? deployments = null)
        {
            _client = client;
            _logger = logger;
            _pending = pending;
            _scheduler = scheduler;
            _outbox = outbox;
            _sender = sender;
            _settings = settings;
            _deployments = deployments;
        }

        /// <summary>
        /// Łagodne zatrzymanie, w tej kolejności: nic nowego nie rusza; trwające zadania kończą się
        /// (do <c>StopTimeoutSeconds</c>); ich wyniki i logi wychodzą z kolejki; dopiero potem
        /// uzgodnienie zamknięcia gniazda i anulowanie pętli. Zadania, które czekały w kolejce
        /// planisty, przepadają - są w <c>Dispatched</c>, więc platforma zwróci je do kolejki, gdy
        /// zobaczy zamknięte gniazdo.
        ///
        /// Uzgodnienie zamknięcia idzie przed anulowaniem pętli. Anulowanie trwającego ReceiveAsync
        /// zrywa gniazdo bez ramki Close (stan Aborted), przez co DisposeAsync klienta nie ma już
        /// czego zamykać, a platforma dowiaduje się o odejściu runnera dopiero, gdy wykryje martwe
        /// TCP - za proxy potrafi to trwać minuty. Tutaj gniazdo jest jeszcze otwarte, a odpowiedź
        /// serwera odbierze trwający odczyt pętli.
        /// </summary>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _stopping = true;
            _scheduler.CompleteAdding();

            var timeout = TimeSpan.FromSeconds(_deployments?.RestartRequested == true ? 0 : _settings.StopTimeoutSeconds);
            if (_scheduler.Running > 0)
            {
                _logger.LogInformation(
                    "Zatrzymywanie: czekam do {Timeout}s na {Running} zadań w toku",
                    timeout.TotalSeconds, _scheduler.Running);

                if (!await _scheduler.WaitForRunningAsync(timeout, cancellationToken))
                {
                    _logger.LogWarning(
                        "Zatrzymywanie: {Running} zadań nie skończyło się w {Timeout}s - ich wyniki przepadną z procesem, platforma zamknie je jako wynik nieznany",
                        _scheduler.Running, timeout.TotalSeconds);
                }
            }

            await WaitForOutboxAsync(timeout);
            await _client.CloseAsync(cancellationToken);
            await base.StopAsync(cancellationToken);
        }

        /// <summary>Czeka, aż nadawca wyśle wszystko, co zakolejkowano - dopóki gniazdo żyje i mieści się w limicie.</summary>
        private async Task WaitForOutboxAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!_sender.IsDrained && _client.Connected() && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            if (!_sender.IsDrained)
            {
                _logger.LogWarning("Zatrzymywanie: kolejka wyjściowa nie została opróżniona - część wiadomości do platformy przepada");
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_deployments is not null)
            {
                await _deployments.Ready.WaitAsync(stoppingToken);
            }

            while (!stoppingToken.IsCancellationRequested && !_stopping)
            {
                try
                {
                    var connect = await _client.Connect();
                    if (!connect.Connected)
                    {
                        _deployments?.Disconnected();
                        await DelayBeforeReconnectAsync(connect, stoppingToken);
                        continue;
                    }

                    _failedConnects = 0;
                    if (connect.Established)
                    {
                        _runMethodFirstConnected = false;
                    }

                    await FirstConnectedAsync();
                    if (!_runMethodFirstConnected)
                    {
                        await _client.CloseAsync(stoppingToken);
                        continue;
                    }

                    // Tylko po faktycznym powrocie: Connect() w zwykłym obrocie pętli zastaje gniazdo
                    // otwarte i nic nie nawiązuje, a wynik wysłany chwilę temu nie jest do ponowienia.
                    if (connect.Established)
                    {
                        _deployments?.Connected();
                        await ResendPendingResultsAsync();

                        // Runner po powrocie ma wolne sloty - zgłasza gotowość od razu, zamiast czekać
                        // na najbliższy obrót dyspozytora po stronie platformy.
                        if (_scheduler.FreeSlots > 0)
                        {
                            await _client.SendQueryOnJob();
                        }
                    }

                    WebSocketReceiveResult result;
                    using var ms = new MemoryStream();
                    var buff = new byte[1024];
                    do
                    {
                        result = await _client.ReceiveAsync(buff, stoppingToken);
                        if (ms.Length + result.Count > _settings.MaxWebSocketMessageBytes)
                        {
                            await _client.CloseAsync(stoppingToken);
                            throw new InvalidDataException($"WebSocket message exceeds {_settings.MaxWebSocketMessageBytes} bytes.");
                        }

                        ms.Write(buff, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // Ramka Close - albo odpowiedź na nasze zamknięcie (wtedy CloseAsync klienta
                        // już nie ma nic do zrobienia), albo serwer zamyka pierwszy: dopowiadamy
                        // uzgodnienie, a kolejny obrót pętli łączy się na nowo.
                        if (_stopping)
                        {
                            _logger.LogDebug("Platforma potwierdziła zamknięcie ({Status})", result.CloseStatus);
                        }
                        else
                        {
                            _logger.LogInformation(
                                                            "Platforma zamknęła połączenie ({Status}: {Description})",
                                                            result.CloseStatus, result.CloseStatusDescription);
                        }

                        await _client.CloseAsync(stoppingToken);
                        continue;
                    }

                    ms.Position = 0;
                    var json = Encoding.UTF8.GetString(ms.ToArray());
                    var message = JsonSerializer.Deserialize<Message>(json, JsonDefaults.Options);

                    // Cokolwiek przyszło od platformy dowodzi, że połączenie żyło po wysyłkach o
                    // niższym numerze sekwencji - te wyniki nie są już do ponawiania (patrz PendingJobReturns).
                    // Deployment ACKs/notices do not acknowledge job results.
                    if (message?.Type == MessageType.Job)
                    {
                        _pending.Confirm(_outbox.NextSeq());
                    }

                    if (message != null)
                    {
                        Handle(message);
                    }

                    _loopErrors = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e) when (_stopping)
                {
                    // Gniazdo zamknięte przez StopAsync w trakcie odczytu - to nie jest błąd pętli.
                    _logger.LogDebug(e, "Main loop interrupted by shutdown");
                    return;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Main loop");
                    await DelayAfterLoopErrorAsync(stoppingToken);
                }
            }
        }
        /// <summary>
        /// Zwłoka po błędzie pętli. Bez niej błąd powtarzający się natychmiast - gniazdo formalnie
        /// otwarte, ale każdy odczyt rzuca - kręci pętlą na pełnym CPU i zalewa log. Narastanie
        /// ogranicza to drugie, gdy przyczyna nie mija.
        /// </summary>
        private async Task DelayAfterLoopErrorAsync(CancellationToken stoppingToken)
        {
            _loopErrors++;

            var delay = Math.Min(
                BaseLoopErrorDelay.TotalMilliseconds * Math.Pow(2, _loopErrors - 1),
                MaxLoopErrorDelay.TotalMilliseconds);

            await Task.Delay(TimeSpan.FromMilliseconds(delay), stoppingToken).ContinueWith(x =>
            {
            });
        }

        /// <summary>
        /// Odczekuje przed kolejną próbą uzgodnienia. Stała zwłoka wystarczała, dopóki jedynym
        /// powodem odmowy było niedostępne Web; odkąd serwer ogranicza tempo (§3 protokołu),
        /// ponawianie w tym samym rytmie odnawia limit własnymi próbami, zamiast dać mu wygasnąć.
        /// </summary>
        private async Task DelayBeforeReconnectAsync(WSClient.ConnectResult result, CancellationToken stoppingToken)
        {
            _failedConnects++;

            TimeSpan delay;
            if (result.RetryAfter is { } retryAfter)
            {
                // Losowa sekunda ponad termin, żeby runnery z tym samym Retry-After nie wróciły razem.
                delay = retryAfter + TimeSpan.FromMilliseconds(Random.Shared.Next(1000));
            }
            else
            {
                var backoff = Math.Min(
                    BaseReconnectDelay.TotalMilliseconds * Math.Pow(2, _failedConnects - 1),
                    MaxReconnectDelay.TotalMilliseconds);

                // Rozrzut, bo limit jest liczony na adres: bez niego runnery zza jednego NAT-u
                // wracałyby zgraną falą i przekraczały go razem, rundę po rundzie.
                delay = TimeSpan.FromMilliseconds(backoff * (0.5 + Random.Shared.NextDouble() * 0.5));
            }

            _logger.LogInformation(
                "Kolejna próba połączenia za {Delay:0.#}s (nieudanych z rzędu: {Failed})",
                delay.TotalSeconds, _failedConnects);

            await Task.Delay(delay, stoppingToken).ContinueWith(x =>
            {
            });
        }

        private async Task FirstConnectedAsync()
        {
            if (_runMethodFirstConnected)
            {
                return;
            }

            _runMethodFirstConnected = await _client.SendInfo();
        }

        /// <summary>
        /// Wyniki, które nie doszły (albo nie wiadomo, czy doszły), idą zaraz po nawiązaniu
        /// połączenia, przed odczytem czegokolwiek - każdy z tym samym <c>attemptId</c>, żeby platforma
        /// rozpoznała próbę (§5.5 protokołu). Nieudana wysyłka zostawia wynik na następne połączenie.
        /// </summary>
        private async Task ResendPendingResultsAsync()
        {
            var pending = _pending.PeekAll();
            if (pending.Count == 0)
            {
                return;
            }

            foreach (var result in pending)
            {
                _logger.LogInformation(
                    "Ponawiam JobReturn ({Status}) zadania {JobId}, próba {AttemptId}, po ponownym połączeniu",
                    result.Status, result.Id, result.AttemptId);

                var sentSeq = await _client.SendJobReturn(result.Id, result.AttemptId, result.Status, result.Data, result.ExecutionVersion);
                if (sentSeq is null)
                {
                    _logger.LogWarning("Ponowienie JobReturn zadania {JobId} nie powiodło się - zostaje na kolejne połączenie", result.Id);
                    continue;
                }

                _pending.MarkSent(result, sentSeq.Value);
            }
        }
        private void Handle(Message message)
        {
            switch (message.Type)
            {
                case MessageType.Job:
                    HandleJob(message);
                    break;
                case MessageType.Deployment:
                case MessageType.DeploymentApprovalResult:
                case MessageType.DeploymentStatusAck:
                    _deployments?.Handle(message);
                    break;
                default:
                    break;
            }
        }
        private void HandleJob(Message message)
        {
            var job = JsonSerializer.Deserialize<MessageJob>(message.Data, JsonDefaults.Options);
            if (job is null)
            {
                _logger.LogWarning("Odebrano przydział bez treści - pominięty");
                return;
            }

            // Tylko przekazanie do planisty: potwierdzenie (§5.3), slot i wykonanie są jego sprawą,
            // a ta pętla ma wrócić do gniazda, zanim platforma wyśle cokolwiek więcej.
            if (!_scheduler.Enqueue(job))
            {
                _logger.LogWarning(
                    "Przydział {JobId} odrzucony - runner się zatrzymuje; platforma zwróci go do kolejki",
                    job.Id);
            }
        }
    }
}
