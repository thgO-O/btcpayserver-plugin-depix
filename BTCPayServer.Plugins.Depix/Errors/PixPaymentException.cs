using System;

namespace BTCPayServer.Plugins.Depix.Errors;

public class PixPaymentException : Exception
{
    public PixPaymentException() { }
    
    public PixPaymentException(string message)
        : base(message) { }
    
    public PixPaymentException(string message, Exception inner)
        : base(message, inner) { }
}