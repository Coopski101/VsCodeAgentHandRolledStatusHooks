var baseUrl = args.Length > 0 ? args[0] : "http://127.0.0.1:17321";

Console.WriteLine($"Connecting to SSE stream at {baseUrl}/events ...");

using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

while (true)
{
    try
    {
        using var stream = await http.GetStreamAsync($"{baseUrl}/events");
        using var reader = new StreamReader(stream);

        Console.WriteLine("Connected. Waiting for events...\n");

        string? currentEvent = null;

        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("event: "))
            {
                currentEvent = line["event: ".Length..];
            }
            else if (line.StartsWith("data: "))
            {
                var data = line["data: ".Length..];
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {currentEvent}");
                Console.WriteLine($"  {data}\n");
                currentEvent = null;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Disconnected: {ex.Message}. Retrying in 3s...");
        await Task.Delay(3000);
    }
}
