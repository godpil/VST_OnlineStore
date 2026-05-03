using Google.Protobuf;
using Grpc.Core;
using System.Buffers;

namespace PilService.Services {
    public class PilServiceImplementation(ILogger<PilServiceImplementation> logger) : PilService.PilServiceBase {

        private static Dictionary<Guid, Tuple<ByteString, PilServiceFileType>> ServiceSpeicher = new Dictionary<Guid, Tuple<ByteString, PilServiceFileType>>();

        public override Task<AktuellesDatumResponse> GetAktuellesDatum(AktuellesDatumRequest request, ServerCallContext context) {
            logger.Log(LogLevel.Information, new EventId(-1, "-not set-"), "Called: " + context.Method);
            var response = new AktuellesDatumResponse();
            if (request != null && request.HasFormat) {
                response.AktuellesDatum = DateTime.Now.ToString(request.Format);
            } else {
                response.AktuellesDatum = DateTime.Now.ToShortDateString().ToString();
            }
            return Task.FromResult(response);
        }
        public override Task<AktuelleUhrzeitResponse> GetAktuelleUhrzeit(AktuelleUhrzeitRequest request, ServerCallContext context) {            
            logger.Log(LogLevel.Information, new EventId(-1, "-not set-"), "Called: " + context.Method);
            var response = new AktuelleUhrzeitResponse();
            TimeOnly time = new TimeOnly(DateTime.Now.Hour, DateTime.Now.Minute, DateTime.Now.Second);            
            if (request != null) {
                if (request.HasIsSommerzeit && request.IsSommerzeit) 
                    time = time.AddHours(1);                
                if (request.HasIs24Stunden && !request.Is24Stunden) 
                    if (DateTime.Now.Hour > 12)
                        time = time.AddHours(-12);
                if (request.HasIsShowSeconds && request.IsShowSeconds) 
                    response.AktuelleUhrzeit = time.ToString("HH:mm:ss");
                else
                    response.AktuelleUhrzeit = time.ToString("HH:mm");
            }
            return Task.FromResult(response);
        }
        public override Task<LadenResponse> GetDatum(LadenRequest request, ServerCallContext context) {
                logger.Log(LogLevel.Information, new EventId(-1, "-not set-"), "Called: " + context.Method);
            if(request != null && Guid.TryParse(request.LoadId, out Guid loadId)) {
                var response = new LadenResponse();
                if (!ServiceSpeicher.ContainsKey(loadId)) {
                    response.Success = false;
                    response.Content = ByteString.Empty;
                    response.Filetype = 0;
                } else {
                    response.Success = true;
                    response.Content = ServiceSpeicher[loadId].Item1;
                    response.Filetype = ServiceSpeicher[loadId].Item2;
                }
                return Task.FromResult(response);
            }else {
                throw new ArgumentNullException(nameof(request));
            }
        }
        public override Task<PilServiceInfoResponse> GetPilServiceInfo(PilServiceInfoRequest request, ServerCallContext context) {
            logger.Log(LogLevel.Information, new EventId(-1, "-not set-"), "Called: " + context.Method);
            var response = new PilServiceInfoResponse();
            response.Info = "PilService v.1.0.0 This is a simple gRPC service that provides the current date and time, as well as the ability to save and load data.";
            return Task.FromResult(response);
        }
        public override Task<SpeichernResonpse> SetDatum(SpeichernRequest request, ServerCallContext context) {
            logger.Log(LogLevel.Information, new EventId(-1, "-not set-"), "Called: " + context.Method);
            try {
                var response = new SpeichernResonpse();
                var id = Guid.NewGuid();
                response.Guid = id.ToString();
                ServiceSpeicher.Add(id, new Tuple<ByteString, PilServiceFileType>(request.Content, request.Filetype));
                response.Success = true;
                return Task.FromResult(response);
            } catch (Exception) {                
                throw;
            }
        }
    }
}
