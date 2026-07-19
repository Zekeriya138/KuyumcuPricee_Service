namespace kuyumcu_application.Abstractions;

public interface ISmsSender
{
    Task SendAsync(string phone, string message, CancellationToken ct = default);
}
