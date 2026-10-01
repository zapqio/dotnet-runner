namespace Zapqio.Runner
{
    public class AppSettings
    {
        public LoggerClass Logger { get; set; } = new();
        public string Token { get; set; }
        public string Name { get; set; }
        public string Url { get; set; }

        /// <summary>
        /// Maksymalny rozmiar całej odbieranej wiadomości WebSocket w bajtach UTF-8.
        /// Ustaw tę samą wartość w platformie. Zmiana wymaga restartu aplikacji.
        /// </summary>
        public int MaxWebSocketMessageBytes { get; set; } = 32 * 1024 * 1024;

        /// <summary>
        /// Ile zadań runner wykonuje naraz. Domyślnie 1, czyli dotychczasowe zachowanie: jedno po
        /// drugim. Wyższa wartość ma sens wyłącznie dla modułów gotowych na równoległe wywołania
        /// <c>Run</c> (także tej samej metody na tej samej instancji) - runner nie dodaje żadnej
        /// synchronizacji, o tym decyduje twórca modułu. To jedyne miejsce, w którym pojemność się
        /// ustawia: runner ogłasza ją platformie w <c>Info</c> (§5.1 protokołu), a platforma wysyła
        /// najwyżej tyle zadań naraz (i może przyciąć od góry, dziś do 32).
        /// Zmienna środowiskowa: <c>ZAPQIO_MAX_CONCURRENCY</c>.
        /// </summary>
        public int MaxConcurrency { get; set; } = 1;

        /// <summary>
        /// Ile sekund przy zatrzymaniu usługi runner czeka na zadania w toku i wysyłkę ich wyników,
        /// zanim zamknie gniazdo. Po tym czasie zadania są porzucane i platforma zamknie je jako
        /// „wynik nieznany". Zero = bez czekania.
        /// </summary>
        public int StopTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Ile linii logu zadań może czekać na wysyłkę, gdy platforma jest niedostępna. Ponad limit
        /// kolejne linie są pomijane (z jedną linią ostrzegawczą w logu zadania), żeby odcięte Web nie
        /// rozdęło pamięci runnera bez końca.
        /// </summary>
        public int MaxQueuedLogLines { get; set; } = 10_000;

        /// <summary>
        /// Od jakiej wagi wpisy MODUŁU idą do platformy: <c>Debug</c>, <c>Info</c> (domyślnie),
        /// <c>Warning</c>, <c>Error</c>, <c>Critical</c>. Wartość spoza listy jest ignorowana - zostaje
        /// <c>Info</c>, czyli zachowanie sprzed tego ustawienia. Zmienna środowiskowa:
        /// <c>ZAPQIO_MIN_LOG_LEVEL</c>.
        /// </summary>
        /// <remarks>
        /// Próg obejmuje wszystkie trzy kanały modułu - <c>stdout</c>, <c>stderr</c> i jawnie nazwany
        /// poziom (<c>RunnerLog</c>, wstrzyknięty <c>ILogger</c>) - i NIE obejmuje wpisów, które runner
        /// generuje sam. Wiersz startowy zadania musi wyjść niezależnie od progu, bo to on przestawia
        /// zadanie na <i>Executing</i>; dlaczego, tłumaczy <see cref="JobLogWriter"/>.
        ///
        /// <c>Debug</c> jest poniżej domyślnego progu celowo: wpis kosztuje jedną ramkę WebSocket i
        /// jedno miejsce w <see cref="MaxQueuedLogLines"/>, więc gadatliwy moduł z włączonym Debugiem
        /// potrafi zapchać kolejkę szybciej, niż nadawca ją opróżni. To ustawienie na czas diagnozy.
        /// </remarks>
        public string MinRemoteLogLevel { get; set; } = nameof(Protocol.Enums.MessageLogLevel.Info);

        /// <summary>
        /// Rozwiązany <see cref="MinRemoteLogLevel"/>. Ustawiane raz, w <see cref="Normalize"/> -
        /// parsowanie przy każdym wpisie byłoby kosztem na ścieżce gorącej. Bez publicznego settera,
        /// żeby wiązanie konfiguracji go nie dotykało.
        /// </summary>
        public Protocol.Enums.MessageLogLevel RemoteLogThreshold { get; private set; } = Protocol.Enums.MessageLogLevel.Info;

        /// <summary>Sprowadza wartości bez sensu do dopuszczalnych; wołane raz, po zbudowaniu ustawień.</summary>
        public void Normalize()
        {
            if (MaxWebSocketMessageBytes <= 0)
            {
                throw new InvalidOperationException("MaxWebSocketMessageBytes must be greater than zero.");
            }

            if (MaxConcurrency < 1)
            {
                MaxConcurrency = 1;
            }

            if (StopTimeoutSeconds < 0) StopTimeoutSeconds = 0;
            if (MaxQueuedLogLines < 100) MaxQueuedLogLines = 100;

            // Literówka w progu nie może uciszyć logów ani wywrócić startu - wraca domyślne Info.
            RemoteLogThreshold = Enum.TryParse<Protocol.Enums.MessageLogLevel>(MinRemoteLogLevel, ignoreCase: true, out var threshold)
                && Enum.IsDefined(threshold)
                ? threshold
                : Protocol.Enums.MessageLogLevel.Info;
            MinRemoteLogLevel = RemoteLogThreshold.ToString();
        }

        public class LoggerClass
        {
            public string LogLevel { get; set; }
            public string PathDirectory { get; set; }
        }
    }

}
