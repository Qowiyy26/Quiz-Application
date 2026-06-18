namespace Quiz_Application.Interface.Service
{
    public interface IEmailService
    {
        Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken);
    }
}
