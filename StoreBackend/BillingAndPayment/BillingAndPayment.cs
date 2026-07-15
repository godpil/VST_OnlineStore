using System;
using System.Collections.Generic;
using System.Text;

namespace StoreBackend.BillingAndPayment {
    public class BillingAndPayment {
        private static BillingAndPayment? _instance;
        private BillingAndPayment() { }
        public static BillingAndPayment Instance {
            get {
                if (_instance == null) {
                    _instance = new BillingAndPayment();
                }
                return _instance;
            }
        }
        
        //Hier die Paymentfassade aufbauen, die die verschiedenen Payment-Methoden kapselt und eine einheitliche Schnittstelle bietet.
    }
}