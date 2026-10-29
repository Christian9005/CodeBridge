namespace CodeBridge.IotBindings;

/// <summary>A chip answered, but it is not the one the block expects (or it needs something else). The message is meant for the person using the flow, so the blocks show it as it is.</summary>
public sealed class I2cChipException : InvalidOperationException
{
    public I2cChipException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
