# CoreIpc
WCF-like service model API for communication over named pipes. .NET and [node.js](https://github.com/UiPath/coreipc/tree/master/src/Clients/nodejs) clients.
- async
- json serialization
- DI integration
- cancellation
- timeouts
- callbacks
- automatic reconnect
- interception
- configurable task scheduler
- client authentication and impersonation

Check [the tests](https://github.com/UiPath/CoreIpc/blob/master/src/UiPath.CoreIpc.Tests/IpcTests.cs) and the sample.
```C#
// configure and start the server
_ = new ServiceHostBuilder(serviceProvider)
    .AddEndpoint(new NamedPipeEndpointSettings<IComputingService>())
    .Build()
    .RunAsync();
// configure the client
var computingClient = 
    new NamedPipeClientBuilder<IComputingService>()
    .Build();
// call a remote method
var result = await computingClient.AddFloat(1.23f, 4.56f, cancellationToken);
```
