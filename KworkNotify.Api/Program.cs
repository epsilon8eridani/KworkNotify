using System.Text;
using DotNetEnv;
using KworkNotify.Core;
using KworkNotify.Core.Auth;
using KworkNotify.Core.Interfaces;
using KworkNotify.Core.Kwork;
using KworkNotify.Core.Service;
using KworkNotify.Core.Service.Backup;
using KworkNotify.Core.Service.Cache;
using KworkNotify.Core.Service.Database;
using KworkNotify.Core.Service.Statistic;
using KworkNotify.Core.Service.Types;
using KworkNotify.Core.Telegram;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Events;
using StackExchange.Redis;

const string outputTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";
var logsPath = Path.Combine(AppContext.BaseDirectory, "logs");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .WriteTo.Console(outputTemplate: outputTemplate)
    .WriteTo.File($"{logsPath}/.log", rollingInterval: RollingInterval.Day, outputTemplate: outputTemplate)
    .WriteTo.File($"{logsPath}/errors-.log", rollingInterval: RollingInterval.Day, outputTemplate: outputTemplate, restrictedToMinimumLevel: LogEventLevel.Error)
    .CreateLogger();

try
{
    Env.Load();
    var cookies = Environment.GetEnvironmentVariable("COOKIES");
    var connectionString = Environment.GetEnvironmentVariable("CONNECTION_STRING");
    var botToken = Environment.GetEnvironmentVariable("BOT_TOKEN");
    var redis = Environment.GetEnvironmentVariable("REDIS");

    if (string.IsNullOrEmpty(cookies) || string.IsNullOrEmpty(connectionString) || string.IsNullOrEmpty(botToken) || string.IsNullOrEmpty(redis))
    {
        throw new ApplicationException("Missing environment variables: COOKIES or CONNECTION_STRING or BOT_TOKEN or REDIS");
    }

    var builder = WebApplication.CreateBuilder(args);

    // ReSharper disable once JoinDeclarationAndInitializer
    string configPath;

#if DEBUG
    configPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "KworkNotify.Core");
#else
    configPath = Directory.GetCurrentDirectory();
#endif

    builder.Configuration
        .SetBasePath(configPath)
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
        .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
        .AddEnvironmentVariables();

    var jwt = builder.Configuration.GetSection(nameof(JwtSettings)).Get<JwtSettings>();
    if (jwt == null)
    {
        throw new ApplicationException("Missing JWT configuration");
    }
    builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection(nameof(JwtSettings)));
    builder.Services.Configure<AppSettings>(builder.Configuration.GetSection(nameof(AppSettings)));
    builder.Services.AddSingleton<IKworkData>(_ => new KworkData()
    {
        Cookies = cookies
    });
    builder.Services.AddSingleton<ITelegramData>(_ => new TelegramData()
    {
        Token = botToken
    });
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis));
    builder.Services.AddSingleton<IAppCache, AppCache>();
    builder.Services.AddSingleton<IMongoContext, MongoContext>(_ => new MongoContext(connectionString));
    builder.Services.AddSingleton<IKworkParser, KworkParser>();
    builder.Services.AddSingleton<KworkService>();
    builder.Services.AddScoped<IApiUserService, ApiUserService>();
    builder.Services.AddSingleton<IBackupManager, BackupManager>();
    builder.Services.AddSingleton<IUsageStatisticManager, UsageStatisticManager>();
    builder.Services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<KworkService>());
    builder.Services.AddHostedService<TelegramService>();
    builder.Services.AddHostedService<BackupScheduler>();
    // builder.Services.AddHostedService<UsageStatisticService>();
    builder.Services.AddSerilog();

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.RequireHttpsMetadata = false;
            options.SaveToken = true;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwt.Issuer,
                ValidAudience = jwt.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret))
            };
        });
    builder.WebHost.UseKestrel();
    builder.Services.AddAuthorization();
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo { Title = "KworkNotify API", Version = "v1" });

        options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "Bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Enter JWT token"
        });

        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "Bearer"
                    }
                },
                []
            }
        });
    });

    var app = builder.Build();

    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    });

    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.RoutePrefix = "api";
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "KworkNotify API V1");
    });

    // app.UseHttpsRedirection();
    app.UseAuthorization();
    app.MapControllers();
    await app.RunAsync();
}
catch (Exception e)
{
    Log.ForContext<Program>().Error(e, "application load error");
}
finally
{
    await Log.CloseAndFlushAsync();
}