using System;
using System.Collections.Generic;
using BTCPayServer.Plugins.Depix.Data.Enums;

namespace BTCPayServer.Plugins.Depix.Data.Models.ViewModels
{
    public class PixTransactionsViewModel
    {
        public List<PixTxResponse> Transactions { get; set; } = [];
    }

    public class PixTxResponse
    {
        public string InvoiceId { get; set; } = "";
        public DateTimeOffset Created { get; set; }
        public string QrId { get; set; } = "";
        public string DepixAddress { get; set; }   
        public int? ValueInCents { get; set; }
        public string DepixStatusRaw { get; set; } = "pending";
        public DepixStatus? DepixStatus { get; set; } 
    }
}