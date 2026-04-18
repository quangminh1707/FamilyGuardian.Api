using System.Text;
using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Hubs;
using FamilyGuardian.Api.Jobs;
using FamilyGuardian.Api.Middleware;
using FamilyGuardian.Api.Proxy;
using FamilyGuardian.Api.Services;
using FamilyGuardian.Api.Services.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Quartz;
using Serilog;

// ─── Serilog early init ────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ─── Serilog ────────────────────────────────────────────────────
    builder.Host.UseSerilog((ctx, lc) =>
        lc.ReadFrom.Configuration(ctx.Configuration)
          .WriteTo.Console()
          .WriteTo.File("logs/log-.txt", rollingInterval: RollingInterval.Day));

    // ─── MySQL + EF Core ────────────────────────────────────────────
    var connStr = builder.Configuration.GetConnectionString("Default")!;
    builder.Services.AddDbContext<AppDbContext>(opt =>
    {
        opt.UseMySql(connStr, ServerVersion.AutoDetect(connStr),
                mysql => mysql.EnableRetryOnFailure());
        opt.UseSnakeCaseNamingConvention(); // Map PascalCase -> snake_case
    });

    // ─── JWT Authentication ─────────────────────────────────────────
    var jwtKey = builder.Configuration["Jwt:SecretKey"]!;
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(opt =>
        {
            opt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
                ValidateIssuer = true,
                ValidIssuer = builder.Configuration["Jwt:Issuer"],
                ValidateAudience = true,
                ValidAudience = builder.Configuration["Jwt:Audience"],
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };

            // Allow SignalR to use token from query string
            opt.Events = new JwtBearerEvents
            {
                OnMessageReceived = ctx =>
                {
                    var accessToken = ctx.Request.Query["access_token"];
                    var path = ctx.HttpContext.Request.Path;
                    if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                        ctx.Token = accessToken;
                    return Task.CompletedTask;
                }
            };
        });

    builder.Services.AddAuthorization();

    // ─── AutoMapper ─────────────────────────────────────────────────
    builder.Services.AddAutoMapper(typeof(Program));

    // ─── HttpClient (website check + proxy forward) ─────────────────
    builder.Services.AddHttpClient("WebCheck", c =>
    {
        c.Timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("WebsiteCheck:TimeoutSeconds", 5));
        c.DefaultRequestHeaders.Add("User-Agent", "FamilyGuardian/1.0");
    }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 3,
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    });

    builder.Services.AddHttpClient("ProxyForward", c =>
    {
        c.Timeout = TimeSpan.FromSeconds(30);
        c.DefaultRequestHeaders.Add("User-Agent", "FamilyGuardian-Proxy/1.0");
    });

    // ─── SignalR ─────────────────────────────────────────────────────
    builder.Services.AddSignalR();

    // ─── Quartz Scheduler ────────────────────────────────────────────
    builder.Services.AddQuartz(q =>
    {
        // Job 1: Send Scheduled Notifications
        var notifJobKey = new JobKey("SendScheduledNotificationsJob");
        q.AddJob<SendScheduledNotificationsJob>(opts => opts.WithIdentity(notifJobKey));
        q.AddTrigger(opts => opts
            .ForJob(notifJobKey)
            .WithIdentity("SendScheduledNotificationsTrigger")
            .WithSimpleSchedule(s => s
                .WithIntervalInMinutes(1)
                .RepeatForever()));

        // Job 2: Close Idle Sessions (new)
        var sessionJobKey = new JobKey("CloseIdleSessionsJob");
        q.AddJob<CloseIdleSessionsJob>(opts => opts.WithIdentity(sessionJobKey));
        q.AddTrigger(opts => opts
            .ForJob(sessionJobKey)
            .WithIdentity("CloseIdleSessionsTrigger")
            .WithSimpleSchedule(s => s
                .WithIntervalInMinutes(1)
                .RepeatForever()));
    });
    builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

    // ─── Application Services ────────────────────────────────────────
    builder.Services.AddScoped<IAuthService, AuthService>();
    builder.Services.AddScoped<IChildService, ChildService>();
    builder.Services.AddScoped<IAllowedWebsiteService, AllowedWebsiteService>();
    builder.Services.AddScoped<IAccessLogService, AccessLogService>();
    builder.Services.AddScoped<INotificationService, NotificationService>();
    builder.Services.AddScoped<IWebsiteCheckService, WebsiteCheckService>();
    builder.Services.AddScoped<IJwtService, JwtService>();
    builder.Services.AddScoped<IOnlineStatusService, OnlineStatusService>();

    // ─── Proxy Services (new) ────────────────────────────────────────
    builder.Services.AddScoped<IProxyAccessChecker, ProxyAccessChecker>();
    builder.Services.AddScoped<ISessionTracker, SessionTracker>();

    // ─── Proxy ───────────────────────────────────────────────────────
    builder.Services.AddScoped<ProxyConnectionHandler>();
    builder.Services.AddHostedService<FamilyProxyServer>();

    // ─── CORS ────────────────────────────────────────────────────────
    builder.Services.AddCors(opt =>
    {
        opt.AddPolicy("AllowFrontend", policy =>
            policy.WithOrigins(
                    "http://localhost:5173",
                    "http://localhost:3000",
                    "http://localhost:5174",
                    "http://localhost:5175")
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials());
    });

    // ─── Controllers ─────────────────────────────────────────────────
    builder.Services.AddControllers()
        .AddJsonOptions(o =>
        {
            o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });

    // ─── Swagger ─────────────────────────────────────────────────────
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "Family Guardian API",
            Version = "v2.0",
            Description = "Parental control system API with Proxy and Stats"
        });

        c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Enter: Bearer {your JWT token}"
        });
        c.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
                },
                Array.Empty<string>()
            }
        });
    });

    // ─── Build ───────────────────────────────────────────────────────
    var app = builder.Build();

    // ─── Pipeline ────────────────────────────────────────────────────
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Family Guardian API v2.0");
        c.RoutePrefix = "swagger";
    });

    app.UseCors("AllowFrontend");
    app.UseMiddleware<ExceptionMiddleware>();
    app.UseMiddleware<RequestLoggingMiddleware>();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
    app.MapHub<NotificationHub>("/hubs/notifications");

    Log.Information("Family Guardian API starting...");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application start-up failed");
}
finally
{
    Log.CloseAndFlush();
}
