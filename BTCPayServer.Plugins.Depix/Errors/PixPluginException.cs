using System;

namespace BTCPayServer.Plugins.Depix.Errors;

public class PixPluginException : Exception
{
    public PixPluginException() { }
    
    public PixPluginException(string message)
        : base(message) { }
    
    public PixPluginException(string message, Exception inner)
        : base(message, inner) { }   
}