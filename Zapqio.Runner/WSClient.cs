using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Zapqio.Runner.Background;
using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner
{
    /// <summary>
    /// Gniazdo do platformy. Uzgodnienie i odczyt wołane są z pętli połączenia; każdy zapis idzie
    /// przez <see cref="Outbox"/> i wykonuje go wyłącznie <see cref="OutboundSender"/> (stąd
    /// <see cref="IOutboundTransport"/>) - <c>ClientWebSocket.SendAsync</c> nie dopuszcza dwóch
    /// zapisów naraz, a przy kilku zadaniach piszą z kilku wątków.
    /// </summary>
    public class WSClient : IAsyncDisposable, IOutboundTransport
    {
        /// <summary>
        /// Wynik próby uzgodnienia. Odmowa nie jest wyjątkiem - dla pętli głównej liczy się tylko to,
        /// jak długo odczekać przed kolejną próbą.
        /// </summary>
        /// <param name="Established">
        /// Prawda tylko wtedy, gdy TO wywołanie nawiązało nowe połączenie. Gniazdo zastane otwarte daje
        /// <see cref="Connected"/> bez <see cref="Established"/> - pętla główna woła <see cref="Connect"/>
        /// w każdym obrocie, a niektóre rzeczy (ponowienie wyniku) mają sens wyłącznie po powrocie.
        /// </param>
        public readonly record struct ConnectResult(bool Connected, HttpStatusCode? Status, TimeSpan? RetryAfter, bool Established = false)
        {
            public static ConnectResult Ok(bool established = false) => new(true, null, null, established);

            public static ConnectResult Failed(HttpStatusCode? status = null, TimeSpan? retryAfter = null)
                => new(false, status, retryAfter);
        }

        /// <summary>Sufit na <c>Retry-After</c> - zepsuta wartość nie może zaparkować runnera na stałe.</summary>
        private const int MaxRetryAfterSeconds = 300;

        private readonly AppSettings _settings;
        private readonly ILogger<WSClient> _logger;
        private readonly MethodsProvider _methodsProvider;
        private readonly Outbox _outbox;
        private readonly RunnerProcessState _process;
        private readonly PendingJobReturns _pending;
        ClientWebSocket _client;

        public List<MessageMethod> Methods { get; private set; }
        public string Name { get; private set; }

        public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffor, CancellationToken cancellationToken) => _client.ReceiveAsync(buffor, cancellationToken);

        public WSClient(AppSettings settings, ILogger<WSClient> logger, MethodsProvider methodsProvider, Outbox outbox,
            RunnerProcessState process, PendingJobReturns pending)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _methodsProvider = methodsProvider;
            _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
            _process = process;
            _pending = pending;
            try
            {
                _client = new ClientWebSocket();
                ConfigureClient(_client, settings);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize WebSocket client");
                throw;
            }
        }
        private void ConfigureClient(ClientWebSocket client, AppSettings settings)
        {
            try
            {
                if (client == null)
                {
                    throw new ArgumentNullException(nameof(client));
                }

                if (settings == null)
                {
                    throw new ArgumentNullException(nameof(settings));
                }

                if (!string.IsNullOrEmpty(settings.Token))
                {
                    client.Options.SetRequestHeader("X-Zapqio-Token", settings.Token);
                }

                if (!string.IsNullOrEmpty(settings.Name))
                {
                    client.Options.SetRequestHeader("X-Zapqio-Name", settings.Name);
                }

                client.Options.SetRequestHeader(ProtocolVersion.Header, ProtocolVersion.Current.ToString());
                client.Options.SetRequestHeader(MessageInfo.ProcessInstanceHeader, _process.InstanceId.ToString());

                // Bez tego po nieudanym uzgadnianiu HttpStatusCode jest 0, a HttpResponseHeaders null
                // - 429 nie do odróżnienia od zerwanego połączenia.
                client.Options.CollectHttpResponseDetails = true;
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException("Invalid header value provided", ex);
            }
        }


        public async Task<ConnectResult> Connect()
        {
            try
            {
                if (_client.State == WebSocketState.Open)
                {
                    return ConnectResult.Ok();
                }

                if (_client.State != WebSocketState.None)
                {
                    _logger.LogInformation("Reconnecting WebSocket");
                    try
                    {
                        _client.Abort();
                        _client.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error during cleanup before reconnection");
                    }
                    _client = new ClientWebSocket();
                    ConfigureClient(_client, _settings);
                }

                if (string.IsNullOrEmpty(_settings.Url))
                {
                    throw new InvalidOperationException("WebSocket URL is not configured");
                }

                var uri = new Uri($"{_settings.Url}/ws-runner");
                var cancel = new CancellationTokenSource(10000);
                await _client.ConnectAsync(uri, cancel.Token);
                _logger.LogInformation("Successfully connected to WebSocket at {Uri}", uri);
                return ConnectResult.Ok(established: true);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Connection attempt timed out after 10 seconds");
                return ConnectResult.Failed();
            }
            catch (UriFormatException ex)
            {
                _logger.LogError(ex, "Invalid WebSocket URL format: {Url}", _settings.Url);
                return ConnectResult.Failed();
            }
            catch (WebSocketException ex)
            {
                return HandshakeRefused(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during connection");
                return ConnectResult.Failed();
            }
        }

        /// <summary>
        /// Wyciąga z nieudanego uzgadniania status i ewentualny termin ponowienia. Statusu może nie
        /// być - gdy do serwera nie doszliśmy, odpowiedzi HTTP nie było.
        /// </summary>
        private ConnectResult HandshakeRefused(WebSocketException ex)
        {
            var status = _client.HttpStatusCode;
            if (status == default)
            {
                _logger.LogError(ex, "WebSocket connection failed");
                return ConnectResult.Failed();
            }

            var retryAfter = ReadRetryAfter();

            if (status == HttpStatusCode.TooManyRequests)
            {
                // Ostrzeżenie, nie błąd: serwer nie doszedł do tokenu (§3), więc nie ma tu czego naprawiać.
                _logger.LogWarning(
                    "Serwer ogranicza tempo uzgodnień (429){RetryAfter}",
                    retryAfter is null ? "" : $", prosi o odczekanie {retryAfter.Value.TotalSeconds:0}s");
            }
            else
            {
                _logger.LogError(ex, "Serwer odrzucił uzgadnianie ze statusem {Status}", (int)status);
            }

            return ConnectResult.Failed(status, retryAfter);
        }

        /// <summary>
        /// Czyta <c>Retry-After</c> jako liczbę sekund (§3 protokołu). Dat HTTP nie rozumiemy - brak
        /// albo niesparsowana wartość znaczy, że o terminie decyduje runner.
        /// </summary>
        private TimeSpan? ReadRetryAfter()
        {
            var headers = _client.HttpResponseHeaders;
            if (headers is null)
            {
                return null;
            }

            var raw = headers
                .FirstOrDefault(h => string.Equals(h.Key, "Retry-After", StringComparison.OrdinalIgnoreCase))
                .Value?.FirstOrDefault();

            if (!int.TryParse(raw, out var seconds) || seconds < 0)
            {
                return null;
            }

            return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryAfterSeconds));
        }

        /// <inheritdoc/>
        public bool IsConnected => Connected();

        /// <summary>
        /// Zapis jednej ramki do gniazda. Woła to wyłącznie <see cref="OutboundSender"/> - wszyscy
        /// inni kolejkują przez <see cref="Outbox"/>, inaczej dwa zapisy naraz wywróciłyby gniazdo.
        /// </summary>
        public async Task<bool> WriteAsync(Message message)
        {
            try
            {
                if (_client.State == WebSocketState.Open)
                {
                    var buff = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, JsonDefaults.Options));

                    var cancel = new CancellationTokenSource(10000);
                    await _client.SendAsync(buff, WebSocketMessageType.Text, true, cancel.Token);
                }
                else
                {
                    _logger.LogWarning($"Cannot send message of type {message.Type}, WebSocket is not open. State: {_client.State}");
                    //nic nie poszło w gniazdo, więc to nie jest sukces - inaczej wołający uzna, że platforma dostała wiadomość
                    return false;
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning($"Sending message of type {message.Type} timed out");
                return false;
            }
            catch (WebSocketException ex)
            {
                _logger.LogError(ex, $"WebSocket error while sending message of type {message.Type}");
                return false;
            }
            catch (ObjectDisposedException)
            {
                _logger.LogError($"WebSocket was disposed while trying to send message of type {message.Type}");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Unexpected error while sending message of type {message.Type}");
                return false;
            }
        }

        /// <summary>Odpytanie o zadanie: „mam wolne miejsce". Wyślij i zapomnij - platforma i tak sama rozsyła co obrót.</summary>
        public void SendDeployment(MessageType type, object payload) => _outbox.Enqueue(Outbox.Frame(type, payload));

        public Task SendQueryOnJob()
        {
            _outbox.Enqueue(Outbox.Frame(MessageType.Job, null));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Potwierdza odbiór przydziału (§5.3). Kolejkowane natychmiast po odebraniu zadania, przed
        /// logiem startowym - platforma bez tego zwróci zadanie do kolejki po upływie terminu.
        /// </summary>
        public Task SendJobAccepted(Guid id, Guid attemptId)
        {
            _outbox.Enqueue(Outbox.Frame(MessageType.JobAccepted, new MessageJobAccepted
            {
                Id = id,
                AttemptId = attemptId
            }));
            return Task.CompletedTask;
        }

        /// <summary>Wynik zadania: numer sekwencji zapisu albo <c>null</c>, gdy nie wyszedł (patrz <see cref="PendingJobReturns"/>).</summary>
        public Task<long?> SendJobReturn(Guid Id, Guid attemptId, MessageResponseStatus status, string data, Zapqio.Deployments.ExecutionVersion? executionVersion = null)
        {
            var m = new MessageJobReturn
            {
                Data = data,
                Id = Id,
                AttemptId = attemptId,
                Status = status,
                ExecutionVersion = executionVersion
            };
            return _outbox.SendAndWaitAsync(Outbox.Frame(MessageType.JobReturn, m));
        }

        /// <summary>Linia logu zadania - kolejkowana, wysyłana w tle, przy odciętym Web czeka na powrót gniazda.</summary>
        public void SendLog(MessageLog log) => _outbox.EnqueueLog(log);

        /// <summary>
        /// Log startowy: metoda rusza dopiero, gdy ta linia faktycznie wyszła z gniazda. Przy
        /// zamkniętym gnieździe kończy się od razu fałszem, a wykonanie jest odwoływane - inaczej
        /// metoda ruszyłaby po powrocie, gdy platforma dawno zwróciła zadanie do kolejki.
        /// </summary>
        public async Task<bool> SendLogAndWaitAsync(MessageLog log) =>
            await _outbox.SendAndWaitAsync(Outbox.Frame(MessageType.Log, log)) is not null;

        public bool Connected()
        {
            return _client?.State == WebSocketState.Open;
        }
        public async Task<bool> SendInfo()
        {
            var l = new List<MessageMethod>();
            var methods = _methodsProvider.GetMethods();
            foreach (var item in methods)
            {
                var m = new MessageMethod
                {
                    Name = item.NameMethod(),
                    In = item.InData() != null ? NJsonSchema.JsonSchema.FromType(item.InData()).ToJson() : null,
                    Out = item.OutData() != null ? NJsonSchema.JsonSchema.FromType(item.OutData()).ToJson() : null,
                };
                l.Add(m);
            }
            var i = new MessageInfo
            {
                Methods = l,
                Name = _settings.Name,
                ProcessInstanceId = _process.InstanceId,
                ActiveAttemptIds = _process.Snapshot(_pending),
                // Pojemność ogłasza runner, bo to on wie, ile zadań uniesie (§5.1) - Web nie ma
                // własnego limitu, przyjmuje tę liczbę i najwyżej przycina od góry.
                MaxConcurrency = _settings.MaxConcurrency
            };

            // Lista idzie do logu, bo "runner w panelu bez metod" to najczęstszy objaw problemu z modułami,
            // a bez tego wpisu nie widać, czy zawinił katalog Modules, ładowanie DLL czy sama platforma.
            if (l.Count == 0)
            {
                _logger.LogWarning(
                    "Wysyłam Info bez żadnej metody - runner nie załadował modułów. Przyczyna powinna być wyżej w logu (wpisy MethodsProvider)");
            }
            else
            {
                _logger.LogInformation(
                    "Wysyłam Info: {Count} metod: {Methods}; pojemność {MaxConcurrency}",
                    l.Count, string.Join(", ", l.Select(x => x.Name)), i.MaxConcurrency);
            }

            return await _outbox.SendAndWaitAsync(Outbox.Frame(MessageType.Info, i)) is not null;
        }
        /// <summary>
        /// Zamyka gniazdo uzgodnieniem - ramka Close w obie strony - żeby platforma od razu wiedziała,
        /// że runner odszedł, zamiast czekać, aż wykryje martwe TCP. Bezpieczne przy trwającym
        /// <see cref="ReceiveAsync"/> z pętli głównej: odpowiedź serwera odbierze ten odczyt, a to
        /// wywołanie na nią zaczeka. Gdy gniazdo nie jest otwarte, nie robi nic.
        /// </summary>
        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            var client = _client;
            if (client is null)
            {
                return;
            }

            if (client.State != WebSocketState.Open && client.State != WebSocketState.CloseReceived)
            {
                return;
            }

            try
            {
                // Anulowanie w trakcie CloseAsync zrywa gniazdo (Abort) - to zamierzone: po upływie
                // limitu i tak nie ma na co czekać.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(5000);
                await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Runner stopping", timeout.Token);
                _logger.LogInformation("WebSocket closed");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Closing WebSocket timed out, aborting the connection");
                client.Abort();
            }
            catch (WebSocketException ex)
            {
                _logger.LogWarning(ex, "WebSocket error while closing, aborting the connection");
                client.Abort();
            }
            catch (ObjectDisposedException)
            {
                _logger.LogDebug("WebSocket was already disposed");
            }
            catch (InvalidOperationException ex)
            {
                // Drugie równoległe zamykanie tego samego gniazda - pierwsze je dokończy.
                _logger.LogDebug(ex, "WebSocket close already in progress");
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await CloseAsync(CancellationToken.None);
                _client?.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed, nothing to do
                _logger.LogDebug("WebSocket was already disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during WebSocket disposal");
                try
                {
                    _client?.Abort();
                    _client?.Dispose();
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }
    }
}
