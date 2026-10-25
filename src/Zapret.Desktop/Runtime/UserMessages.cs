using System.Net.Http;

namespace Zapret.Desktop.Runtime;

internal static class UserMessages
{
    // Keep the original exception in the log; explain the next step in the interface.
    internal static string Error(Exception error) => error switch
    {
        UnauthorizedAccessException => "Windows не разрешила доступ к файлу или папке. Проверьте права доступа или выберите другую папку.",
        FileNotFoundException or DirectoryNotFoundException => "Нужный файл или папка не найдены. Проверьте путь. Если ошибка возникла при запуске обхода, распакуйте полный архив программы вместе со всеми папками.",
        PathTooLongException => "Путь к файлу слишком длинный. Выберите папку с более коротким путём, например C:\\x2zapret.",
        HttpRequestException => "Не удалось связаться с сервером. Проверьте интернет и повторите действие позже.",
        _ when error.Message.Any(c => c is >= 'А' and <= 'я' or 'Ё' or 'ё') => error.Message,
        TimeoutException => "Программа или сервер не ответили за отведённое время. Попробуйте ещё раз.",
        _ => "Не удалось выполнить действие. Если ошибка повторяется, сохраните это техническое сообщение: " + error.Message
    };
}
