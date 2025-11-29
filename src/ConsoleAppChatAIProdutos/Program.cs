using Azure.Monitor.OpenTelemetry.Exporter;
using Bogus;
using ConsoleAppChatAIProdutos.Data;
using ConsoleAppChatAIProdutos.Inputs;
using ConsoleAppChatAIProdutos.Plugins;
using ConsoleAppChatAIProdutos.Tracing;
using ConsoleAppChatAIProdutos.Utils;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Testcontainers.PostgreSql;

Console.WriteLine("***** Testes com Semantic Kernel + Plugins (Kernel Functions) + PostgreSQL *****");
Console.WriteLine();

var numberOfRecords = InputHelper.GetNumberOfNewProducts();

var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json")
    .AddEnvironmentVariables()
    .Build();

CommandLineHelper.Execute("docker images",
    "Imagens antes da execucao do Testcontainers...");
CommandLineHelper.Execute("docker container ls",
    "Containers antes da execucao do Testcontainers...");

Console.WriteLine("Criando container para uso do PostgreSQL...");
var postgresContainer = new PostgreSqlBuilder()
    .WithImage("postgres:18.1")
    .WithDatabase("basecatalogo")
    .WithResourceMapping(
        DBFileAsByteArray.GetContent("basecatalogo.sql"),
        "/docker-entrypoint-initdb.d/01-basecatalogo.sql")
    .Build();
await postgresContainer.StartAsync();

CommandLineHelper.Execute("docker images",
    "Imagens apos execucao do Testcontainers...");
CommandLineHelper.Execute("docker container ls",
    "Containers apos execucao do Testcontainers...");

var connectionString = postgresContainer.GetConnectionString();
Console.WriteLine($"Connection String da base de dados PostgreSQL: {connectionString}");
CatalogoContext.ConnectionString = connectionString;

var db = new DataConnection(new DataOptions().UsePostgreSQL(connectionString));

var random = new Random();
var fakeProdutos = new Faker<ConsoleAppChatAIProdutos.Data.Fake.Produto>("pt_BR").StrictMode(false)
            .RuleFor(p => p.Nome, f => f.Commerce.Product())
            .RuleFor(p => p.CodigoBarras, f => f.Commerce.Ean13())
            .RuleFor(p => p.Preco, f => random.Next(10, 30))
            .Generate(numberOfRecords);


Console.WriteLine($"Gerando {numberOfRecords} produtos...");
await db.BulkCopyAsync<ConsoleAppChatAIProdutos.Data.Fake.Produto>(fakeProdutos);
Console.WriteLine($"Produtos gerados com sucesso!");
Console.WriteLine();
var resultSelectProdutos = await postgresContainer.ExecScriptAsync(
    "SELECT * FROM \"Produtos\"");
Console.WriteLine(resultSelectProdutos.Stdout);

var resourceBuilder = ResourceBuilder
    .CreateDefault()
    .AddService(OpenTelemetryExtensions.ServiceName);

AppContext.SetSwitch("Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnosticsSensitive", true);

var traceProvider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(resourceBuilder)
    .AddSource(OpenTelemetryExtensions.ServiceName)
    .AddSource("Microsoft.SemanticKernel*")
    .AddEntityFrameworkCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddAzureMonitorTraceExporter(options =>
    {
        options.ConnectionString = configuration.GetConnectionString("AppInsights");
    })
    .Build();


var kernelBuilder = Kernel.CreateBuilder();
kernelBuilder.AddAzureOpenAIChatCompletion(
    deploymentName: configuration["AzureOpenAI:DeploymentName"]!,
    endpoint: configuration["AzureOpenAI:Endpoint"]!,
    apiKey: configuration["AzureOpenAI:ApiKey"]!,
    serviceId: "chat");
PromptExecutionSettings settings = new OpenAIPromptExecutionSettings
{
    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
};

kernelBuilder.Plugins.AddFromType<ProdutosPlugin>();
Kernel kernel = kernelBuilder.Build();

var oldForegroundColor = Console.ForegroundColor;
var aiChatService = kernel.GetRequiredService<IChatCompletionService>();
var chatHistory = new ChatHistory();
while (true)
{
    Console.WriteLine("Sua pergunta:");
    var userPrompt = Console.ReadLine();

    using var activity1 = OpenTelemetryExtensions.ActivitySource
        .StartActivity("PerguntaChatIAProdutos")!;

    chatHistory.Add(new ChatMessageContent(AuthorRole.User, userPrompt));

    Console.WriteLine();
    Console.WriteLine("Resposta da IA:");
    Console.WriteLine();

    ChatMessageContent chatResult = await aiChatService
        .GetChatMessageContentAsync(chatHistory, settings, kernel);
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine(chatResult.Content);
    chatHistory.Add(new ChatMessageContent(AuthorRole.Assistant, chatResult.Content));
    Console.ForegroundColor = oldForegroundColor;

    Console.WriteLine();
    Console.WriteLine();

    activity1.Stop();
}