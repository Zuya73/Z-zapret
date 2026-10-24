namespace Zapret.Core;

public static class ProbeClassifier
{
    public static (ProbeStatus Status, string Detail) Classify(int exitCode, int code, long downloaded, long uploaded,
        bool dpi, int[]? acceptedCodes, string error)
    {
        if (exitCode is 2 or 4) return (ProbeStatus.Unsupported, "Системный curl не поддерживает требуемую возможность.");
        if (exitCode == 6) return (ProbeStatus.Failed, "DNS: не удалось определить адрес сервера.");
        if (exitCode == 60) return (ProbeStatus.Failed, "Не пройдена проверка сертификата HTTPS.");
        if (exitCode == 35) return (ProbeStatus.Failed, "Ошибка TLS-handshake; причина не определена.");
        if (dpi)
        {
            if (exitCode == 0 && code is >= 100 and < 600 && uploaded >= 65536 && downloaded >= 32768)
                return (ProbeStatus.Passed, "Передача данных прошла в обоих направлениях.");
            if (exitCode == 28 && uploaded >= 16384 && downloaded <= 20480)
                return (ProbeStatus.Failed, "Тайм-аут после начала передачи. Возможная фильтрация или сбой сети.");
            return (exitCode == 0 ? ProbeStatus.Inconclusive : ProbeStatus.Failed,
                "Недостаточно переданных данных для подтверждения результата.");
        }
        if (exitCode != 0)
            return (ProbeStatus.Failed, $"Ошибка curl {exitCode}: {error.Trim()[..Math.Min(error.Trim().Length, 180)]}");
        bool accepted = acceptedCodes?.Contains(code) ?? code is >= 200 and < 400;
        return (accepted ? ProbeStatus.Passed : ProbeStatus.Inconclusive,
            accepted ? "HTTPS-запрос выполнен. Это не проверка видео или голосового чата." :
                $"Сервер ответил HTTP {code}; доступность сервиса не подтверждена.");
    }
}
