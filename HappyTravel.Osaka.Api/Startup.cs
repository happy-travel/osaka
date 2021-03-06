using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using HappyTravel.ErrorHandling.Extensions;
using HappyTravel.Osaka.Api.Conventions;
using HappyTravel.Osaka.Api.Filters;
using HappyTravel.Osaka.Api.Filters.Authorization;
using HappyTravel.Osaka.Api.Infrastructure;
using HappyTravel.Osaka.Api.Infrastructure.Extensions;
using HappyTravel.Osaka.Api.Options;
using HappyTravel.Osaka.Api.Services;
using HappyTravel.Osaka.Api.Services.Locations;
using HappyTravel.StdOutLogger.Extensions;
using HappyTravel.Telemetry.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Localization.Routing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

namespace HappyTravel.Osaka.Api
{
    public class Startup
    {
        public Startup(IConfiguration configuration, IWebHostEnvironment hostEnvironment)
        {
            _configuration = configuration;
            _hostEnvironment = hostEnvironment;
        }
        
        
        public void ConfigureServices(IServiceCollection services)
        {
            using var vaultClient = VaultHelper.CreateVaultClient(_configuration);
            var token = _configuration[_configuration["Vault:Token"]];
            vaultClient.Login(token).GetAwaiter().GetResult();
            var locationIndexes = vaultClient.Get(_configuration["Elasticsearch:Indexes"]).GetAwaiter().GetResult();
            services.AddElasticsearchClient(_configuration, vaultClient, locationIndexes)
                .AddHttpClients(_configuration, _hostEnvironment, vaultClient)
                .AddResponseCompression()
                .AddCors()
                .AddLocalization()
                .AddTracing(_configuration, options =>
                {
                    options.ServiceName = $"{_hostEnvironment.ApplicationName}-{_hostEnvironment.EnvironmentName}";
                    options.JaegerHost = _hostEnvironment.IsLocal()
                        ? _configuration.GetValue<string>("Jaeger:AgentHost")
                        : _configuration.GetValue<string>(_configuration.GetValue<string>("Jaeger:AgentHost"));
                    options.JaegerPort = _hostEnvironment.IsLocal()
                        ? _configuration.GetValue<int>("Jaeger:AgentPort")
                        : _configuration.GetValue<int>(_configuration.GetValue<string>("Jaeger:AgentPort"));
                })
                .AddControllers()
                .AddJsonOptions(options =>
                {
                    options.JsonSerializerOptions.WriteIndented = false;
                    options.JsonSerializerOptions.IgnoreNullValues = true;
                    options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
                    options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals |
                                                                   JsonNumberHandling.AllowReadingFromString;
                    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
                    options.JsonSerializerOptions.Converters.Add(new NetTopologySuite.IO.Converters.GeoJsonConverterFactory());
                });
            
            services.ConfigureAuthentication(_configuration, _hostEnvironment, vaultClient);
            services.AddHttpContextAccessor();
            services.AddHealthChecks();
            
            services.AddProblemDetailsErrorHandling(); 
            
            services.AddApiVersioning(options =>
            {
                options.AssumeDefaultVersionWhenUnspecified = false;
                options.DefaultApiVersion = new ApiVersion(1, 0);
                options.ReportApiVersions = true;
            });
            
            services.AddSwaggerGen(options =>
            {
                options.SwaggerDoc("v1.0", new OpenApiInfo {Title = "HappyTravel.com Location Service API", Version = "v1.0"});

                var xmlCommentsFileName = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
                var xmlCommentsFilePath = Path.Combine(AppContext.BaseDirectory, xmlCommentsFileName);
                options.CustomSchemaIds(t => t.FullName);
                options.IncludeXmlComments(xmlCommentsFilePath);
                options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                {
                    Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
                    Name = "Authorization",
                    In = ParameterLocation.Header,
                    Type = SecuritySchemeType.ApiKey
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
                            },
                            Scheme = "oauth2",
                            Name = "Bearer",
                            In = ParameterLocation.Header,
                        },
                        Array.Empty<string>()
                    }
                });
            });

            services.AddMvcCore(options =>
                {
                    options.Conventions.Add(new AuthorizeControllerModelConvention());
                    options.Filters.Add(new MiddlewareFilterAttribute(typeof(LocalizationPipelineFilter)));
                    options.Filters.Add(typeof(ModelValidationFilter));
                })
                .AddAuthorization(options =>
                {
                    options.AddPolicy(Policies.OnlyManagerClient , 
                        policy => policy.Requirements.Add(new OnlyManagerClientRequirement()));
                })
                .AddControllersAsServices()
                .AddMvcOptions(m => m.EnableEndpointRouting = true)
                .AddFormatterMappings()
                .AddApiExplorer()
                .AddCacheTagHelper()
                .AddDataAnnotations();
            
            services.AddTransient<IPredictionsService, PredictionsService>();
            services.AddTransient<ILocationsService, LocationsService>();
            services.AddSingleton<IPredictionsManagementService, PredictionsManagementService>();
            
            services.Configure<RequestLocalizationOptions>(options =>
            {
                options.DefaultRequestCulture = new RequestCulture("en");
                options.SupportedCultures = new[]
                {
                    new CultureInfo("en"),
                    new CultureInfo("ar"),
                    new CultureInfo("ru")
                };

                options.RequestCultureProviders.Insert(0, new RouteDataRequestCultureProvider { Options = options });
            });
            services.Configure<IndexOptions>(o => o.Indexes = locationIndexes);

            services.AddHealthChecks().AddCheck<ElasticSearchHealthCheck>("ElasticSearch");
            
            services.AddPredictionsUpdate(vaultClient, _configuration, _hostEnvironment);
        }

        
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILoggerFactory loggerFactory, IOptions<RequestLocalizationOptions> localizationOptions)
        {
            var logger = loggerFactory.CreateLogger<Startup>();
            app.UseProblemDetailsExceptionHandler(env, logger);
            app.UseRequestLocalization(localizationOptions.Value);
            app.UseHttpContextLogging(
                options => options.IgnoredPaths = new HashSet<string> {"/health"}
            );

            app.UseSwagger()
                .UseSwaggerUI(options =>
                {
                    options.SwaggerEndpoint("/swagger/v1.0/swagger.json", "HappyTravel.com Prediction Service API");
                    options.RoutePrefix = string.Empty;
                });

            app.UseResponseCompression();
            app.UseCors(builder => builder
                    .AllowAnyOrigin()
                    .AllowAnyHeader()
                    .AllowAnyMethod());
            
            app.UseHealthChecks("/health");
            app.UseRouting()
                .UseAuthentication()
                .UseAuthorization()
                .UseEndpoints(endpoints =>
                {
                    endpoints.MapControllers();
                });
        }

       
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _hostEnvironment;
    }
}