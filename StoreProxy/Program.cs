namespace StoreProxy {
    public class Program {
        public static void Main(string[] args) {
            //Hier builder aufbauen und YARP-Proxy einstellen
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddReverseProxy()
                .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
            var app = builder.Build();
            app.MapReverseProxy();
            app.MapGet("/", () => "Hello World!");
            app.Run();
        }
    }
}
