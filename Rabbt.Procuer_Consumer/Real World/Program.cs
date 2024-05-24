using Consumer;
using Microsoft.AspNetCore.Builder;
using Serilog;

var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json")
    .AddJsonFile($"appsettings.{environment}.json", true)
    .Build();

// Configure the logging
Log.Logger = new LoggerConfiguration()
    .ReadFrom
    .Configuration(configuration)
    .CreateLogger();

AppDomain.CurrentDomain.ProcessExit +=
    (sender, eventArgs) =>
    {
        Log.CloseAndFlush();

        Console.WriteLine("proc exit");
    };

Host.CreateDefaultBuilder(args)
    .UseSerilog()
    .ConfigureServices(AddServices)
    .Build()
    .Run();

void AddServices(IServiceCollection services)
{
    services.AddWindowsService(options => options.ServiceName = $"RuleValidation Consumer ({environment})");
    services.AddHostedService<RabbitMQHostedConsumer>();
}