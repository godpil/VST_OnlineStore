using StoreBackend.BillingBackend;
using System.Transactions;

namespace StoreBackend {

    namespace BillingBackend{
        public interface IBillingBackend {
            //Lesend
            bool WasPaymentSuccessful(decimal amount, string currency, string paymentMethod);

            //Operativ
            IPaymentResponse ProcessPayment(decimal amount, string currency, string paymentMethod);
            void SetPSP(IPSP psp);
        }
        //Payment Service Provider
        public interface IPSP {
            string Name { get; set; }
            Guid Id { get; set; }
        }
        public interface IPaymentResponse {
            Guid Id { get; set; }
            bool Success { get; }
            string Message { get; }
        }
    }
    
    namespace InvoiceBackend {
        public interface IInvoiceBackend {
            //Operativ
            IBill CreatePDFBilling(decimal amount, string currency, string paymentMethod);
        }

        public interface IBill {
            decimal Amount { get; set; }
            string Currency { get; set; }
            string PaymentMethod { get; set; }
            DateTime Date { get; set; }
        }

    }

    namespace WarehouseBackend {
        
        public interface IArticle {
            Guid ArticleId { get; set; }
            decimal Price { get; set; }
            string Name { get; set; }
            bool IsInStock { get; set; }
            bool IsReserved { get; set; }
        }

        public interface IWarehouseBackend {
            //Lesend
            bool IsArticleInStock(Guid articleId);
            IArticle GetArticle(Guid articleId);
            int GetArticleCount(Guid articleId);

            //Schreibend
            void DeleteArticle(Guid articleId);
            void ReserveArticle(Guid articleId);
            void DereserveArticle(Guid articleId);
            void InsertArticle(Guid articleId, IArticle article);
        }
    }

    public interface IStoreBackend {
        //Brauchen wir hier überhaupt Methoden?
    }
}
