namespace Zapret.Core;

public static class ProbeClassifier
{
    public static (ProbeStatus Status, string Detail) Classify(int exitCode, int code, long downloaded, long uploaded,
        bool dpi, int[]? acceptedCodes, string error)
    {
        if (exitCode is 2 or 4) return (ProbeStatus.Unsupported, "В этой версии Windows недоступен нужный способ сетевой проверки (curl). Попробуйте другой режим проверки.");
        if (exitCode == 6) return (ProbeStatus.Failed, "DNS: не удалось определить адрес сервера.");
        if (exitCode == 60) return (ProbeStatus.Failed, "Не удалось подтвердить защищённое соединение с сайтом. Проверьте дату и время Windows, затем повторите проверку.");
        if (exitCode == 35) return (ProbeStatus.Failed, "Не удалось установить защищённое соединение с сервером (TLS). Причина пока неизвестна.");
        if (dpi)
        {
            if (exitCode == 0 && code is >= 100 and < 600 && uploaded >= 65536 && downloaded >= 32768)
                return (ProbeStatus.Passed, "Передача данных прошла в обоих направлениях.");
            if (exitCode == 28 && uploaded >= 16384 && downloaded <= 20480)
                return (ProbeStatus.Failed, "Передача началась, но сервер перестал отвечать. Возможны ограничения или сбой сети.");
            return (exitCode == 0 ? ProbeStatus.Inconclusive : ProbeStatus.Failed,
                "Передано слишком мало данных, чтобы считать проверку успешной.");
        }
        if (exitCode != 0)
            return (ProbeStatus.Failed, $"Ошибка curl {exitCode}: {error.Trim()[..Math.Min(error.Trim().Length, 180)]}");
        bool accepted = acceptedCodes?.Contains(code) ?? code is >= 200 and < 400;
        return (accepted ? ProbeStatus.Passed : ProbeStatus.Inconclusive,
            accepted ? "Сервер ответил на защищённый запрос. Видео и голосовой чат этой проверкой не проверяются." :
                $"Сервер ответил HTTP {code}; доступность сервиса не подтверждена.");
    }
}
