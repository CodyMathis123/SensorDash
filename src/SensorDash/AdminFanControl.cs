using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

public static class AdminFanControlClient
{
    public const string PipeName = "SensorDash.Fan.Admin.v1";
    // Only an elevated caller can connect to the separate control pipe.
    public static async Task SendAsync(FanCommand command)
    {
        using var wait=new CancellationTokenSource(TimeSpan.FromSeconds(7));
        using var pipe=new NamedPipeClientStream(".",PipeName,PipeDirection.InOut,PipeOptions.Asynchronous);
        await pipe.ConnectAsync(wait.Token);
        using var writer=new StreamWriter(pipe,new UTF8Encoding(false),4096,leaveOpen:true){AutoFlush=true};
        using var reader=new StreamReader(pipe,Encoding.UTF8,false,4096,leaveOpen:true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(command,PipeProtocol.Json).AsMemory(),wait.Token);
        var result=await reader.ReadLineAsync(wait.Token);
        if(string.IsNullOrEmpty(result))throw new IOException("No privileged control response.");
        using var response=JsonDocument.Parse(result);
        if(!response.RootElement.TryGetProperty("accepted",out var accepted)||
            accepted.ValueKind!=JsonValueKind.True)
            throw new UnauthorizedAccessException(response.RootElement.TryGetProperty("message",out var msg)
                ? msg.GetString() : "Collector refused the command.");
    }
}

public sealed class AdminFanControlPipeServer(
    DashboardState state, FanControlRuntime control, ILogger<AdminFanControlPipeServer> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var permissions=new PipeSecurity();
        permissions.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid,null),
            PipeAccessRights.ReadWrite,AccessControlType.Deny));
        permissions.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),
            PipeAccessRights.FullControl,AccessControlType.Allow));
        permissions.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null),
            PipeAccessRights.ReadWrite,AccessControlType.Allow));
        // Unlike the telemetry pipe, there is no general-user allow rule.
        while(!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server=null;
            try
            {
                server=NamedPipeServerStreamAcl.Create(AdminFanControlClient.PipeName,
                    PipeDirection.InOut,NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096,permissions);
                await server.WaitForConnectionAsync(stoppingToken);
                var accepted=server;
                server=null;
                // A single request is sufficient. The hardware worker remains
                // the only thread allowed to manipulate LibreHardwareMonitor.
                _=Task.Run(()=>HandleAsync(accepted,stoppingToken),CancellationToken.None);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) {}
            catch(Exception ex)
            {
                logger.LogWarning(ex,"Administrator-only fan-control IPC unavailable");
                try{await Task.Delay(1000,stoppingToken);}catch(OperationCanceledException){}
            }
            finally{server?.Dispose();}
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe,CancellationToken shutdown)
    {
        await using(pipe)
        {
            try
            {
                using var time=new CancellationTokenSource(TimeSpan.FromSeconds(7));
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(time.Token,shutdown);
                using var input=new StreamReader(pipe,Encoding.UTF8,false,4096,leaveOpen:true);
                using var output=new StreamWriter(pipe,new UTF8Encoding(false),4096,leaveOpen:true){AutoFlush=true};
                var data=await input.ReadLineAsync(timeout.Token);
                if(data==null||data.Length>4096)throw new InvalidDataException("Invalid control command.");
                var command=JsonSerializer.Deserialize<FanCommand>(data,PipeProtocol.Json);
                var result=control.Request(command,state.Settings);
                await output.WriteLineAsync(JsonSerializer.Serialize(result,PipeProtocol.Json).AsMemory(),timeout.Token);
            }
            catch(Exception ex) when(ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,"Rejected privileged fan command");
            }
        }
    }
}
