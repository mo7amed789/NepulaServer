using System.IO.Compression;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.IdentityModel.Tokens;
using NebulaServer.Helpers;
namespace NebulaServer.Extensions;

public static class WebServiceCollectionExtensions
{
    public static void AddNebulaWebServices(this WebApplicationBuilder builder)
    {
        var jwtSecret = builder.Configuration["Auth:JwtSecret"];
        if (string.IsNullOrWhiteSpace(jwtSecret))
        {
            var secretPath = Path.Combine(RuntimePaths.Data, "jwt.secret");
            try
            {
                if (File.Exists(secretPath))
                {
                    jwtSecret = File.ReadAllText(secretPath).Trim();
                }
                if (string.IsNullOrWhiteSpace(jwtSecret) || jwtSecret.Length < 32)
                {
                    jwtSecret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
                    File.WriteAllText(secretPath, jwtSecret);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Unable to create or load the signing secret at '{secretPath}'. Configure Auth:JwtSecret securely or grant the application access to its data directory.", ex);
            }

            builder.Configuration["Auth:JwtSecret"] = jwtSecret;
        }

        if (jwtSecret.Length < 32)
        {
            throw new InvalidOperationException("Auth:JwtSecret must contain at least 32 characters.");
        }

        var key = System.Text.Encoding.UTF8.GetBytes(jwtSecret);

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwtOptions =>
            {
                jwtOptions.RequireHttpsMetadata = false;
                jwtOptions.SaveToken = true;
                jwtOptions.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero
                };

                jwtOptions.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        var path = context.HttpContext.Request.Path;
                        if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/dashboardHub"))
                        {
                            context.Token = accessToken;
                        }
                        return Task.CompletedTask;
                    }
                };
            });
        builder.Services.AddAuthorization();

        builder.Services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
        });
        builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
        {
            options.Level = CompressionLevel.Fastest;
        });
        builder.Services.Configure<GzipCompressionProviderOptions>(options =>
        {
            options.Level = CompressionLevel.Fastest;
        });

        builder.Services.Configure<ForwardedHeadersOptions>(opts =>
        {
            opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            opts.KnownProxies.Clear();
            foreach (var value in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? Array.Empty<string>())
            {
                if (System.Net.IPAddress.TryParse(value, out var address))
                {
                    opts.KnownProxies.Add(address);
                }
            }
        });

        var permitLimit = builder.Configuration.GetValue<int>("RateLimiting:PermitLimit", 100);
        var windowSeconds = builder.Configuration.GetValue<int>("RateLimiting:WindowSeconds", 60);

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var partitionKey = context.User.Identity?.IsAuthenticated == true
                    ? context.User.Identity.Name ?? context.User.FindFirst("sub")?.Value ?? "unknown-user"
                    : context.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip";

                return RateLimitPartition.GetFixedWindowLimiter(partitionKey, partition => new FixedWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromSeconds(windowSeconds)
                });
            });

            options.AddPolicy("StrictAuth", context =>
            {
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip";
                return RateLimitPartition.GetFixedWindowLimiter(ip, partition => new FixedWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(5)
                });
            });
        });

    }
}
