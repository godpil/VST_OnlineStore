namespace InvoiceService.Email;

public sealed class InvoiceEmailOptions {
    public const string SectionName = "InvoiceEmail";

    public string Mode { get; set; } = "Pickup";
    public string SenderAddress { get; set; } = "rechnung@holzwerk.example";
    public string SenderName { get; set; } = "Holzwerk Online Store";
    public string PickupDirectory { get; set; } = "Data/email-outbox";
    public SmtpEmailOptions Smtp { get; set; } = new();

    public bool UsesSmtp => Mode.Equals("Smtp", StringComparison.OrdinalIgnoreCase);

    public void Validate() {
        if (!Mode.Equals("Pickup", StringComparison.OrdinalIgnoreCase)
            && !UsesSmtp) {
            throw new InvalidOperationException(
                "InvoiceEmail:Mode muss 'Pickup' oder 'Smtp' sein.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(SenderAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(SenderName);
        if (UsesSmtp) {
            Smtp.Validate();
        }
        else {
            ArgumentException.ThrowIfNullOrWhiteSpace(PickupDirectory);
        }
    }
}

public sealed class SmtpEmailOptions {
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public void Validate() {
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);
        ArgumentOutOfRangeException.ThrowIfLessThan(Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Port, 65535);
        ArgumentException.ThrowIfNullOrWhiteSpace(UserName);
        ArgumentException.ThrowIfNullOrWhiteSpace(Password);
    }
}
