using System;
using System.Reflection;
using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using Microsoft.IdentityModel.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using DotNetCore.CAP.Dashboard.NodeDiscovery;
using HealthChecks.UI.Client;
using Autofac;
using AutoMapper;
using Adnc.Infr.Common;
using Adnc.Infr.Consul.Registration;
using Adnc.Cus.WebApi.Helper;
using Adnc.Cus.Application;
using Adnc.WebApi.Shared;
using Adnc.WebApi.Shared.Middleware;

namespace Adnc.Cus.WebApi
{
    public class Startup
    {
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _cfg;
        private readonly ServiceInfo _serviceInfo;
        private ServiceRegistrationHelper _srvRegistration;

        public Startup(IConfiguration cfg
            , IWebHostEnvironment env)
        {
            _cfg = cfg;
            _env = env;
            _serviceInfo = ServiceInfo.Create(Assembly.GetExecutingAssembly());
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddScoped<UserContext>();
            services.AddAutoMapper(typeof(AdncCusProfile));
            services.AddHttpContextAccessor();

            _srvRegistration = new ServiceRegistrationHelper(_cfg, services, _env, _serviceInfo);
            _srvRegistration.Configure();
            _srvRegistration.AddControllers();
            _srvRegistration.AddJWTAuthentication();
            _srvRegistration.AddAuthorization<PermissionHandlerRemote>();
            _srvRegistration.AddCors();
            _srvRegistration.AddHealthChecks();
            _srvRegistration.AddEfCoreContext();
            _srvRegistration.AddMongoContext();
            _srvRegistration.AddCaching();
            _srvRegistration.AddSwaggerGen();
            _srvRegistration.AddAllRpcServices();
            _srvRegistration.AddAllEventBusSubscribers();
        }

        public void ConfigureContainer(ContainerBuilder builder)
        {
            //注册依赖模块
            builder.RegisterModule<Adnc.Infr.Mongo.AdncInfrMongoModule>();
            builder.RegisterModule<Adnc.Infr.EfCore.AdncInfrEfCoreModule>();
            builder.RegisterModule(new Adnc.Cus.Application.AdncCusApplicationModule());
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IServiceProvider serviceProvider)
        {
            DefaultFilesOptions defaultFilesOptions = new DefaultFilesOptions();
            defaultFilesOptions.DefaultFileNames.Clear();
            defaultFilesOptions.DefaultFileNames.Add("index.html");
            app.UseDefaultFiles(defaultFilesOptions);
            app.UseStaticFiles();
            if (env.IsDevelopment())
            {
                //开启验证异常显示
                //PII is hidden 异常处理
                IdentityModelEventSource.ShowPII = true;
                app.UseDeveloperExceptionPage();
            }
            app.UseRealIp(x =>
            {
                //new string[] { "X-Real-IP", "X-Forwarded-For" }
                x.HeaderKeys = new string[] { "X-Forwarded-For", "X-Real-IP" };
            });
            app.UseCors(_serviceInfo.CorsPolicy);
            app.UseSwagger(c =>
            {
                c.RouteTemplate = $"/{_serviceInfo.ShortName}/swagger/{{documentName}}/swagger.json";
                c.PreSerializeFilters.Add((swaggerDoc, httpReq) =>
                {
                    swaggerDoc.Servers = new List<OpenApiServer> { new OpenApiServer { Url = $"/", Description = _serviceInfo.Description } };
                });
            });
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint($"/{_serviceInfo.ShortName}/swagger/{_serviceInfo.Version}/swagger.json", $"{_serviceInfo.FullName}-{_serviceInfo.Version}");
                c.RoutePrefix = $"{_serviceInfo.ShortName}";
            });
            //app.UseErrorHandling();
            app.UseHealthChecks($"/{_srvRegistration.GetConsulConfig().HealthCheckUrl}", new HealthCheckOptions()
            {
                Predicate = _ => true,
                // 该响应输出是一个json，包含所有检查项的详细检查结果
                ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
            });
            app.UseRouting();
            app.UseAuthentication();
            app.UseSSOAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers().RequireAuthorization();
            });
            if (env.IsProduction() || env.IsStaging())
            {
                var consulOption = _srvRegistration.GetConsulConfig();
                //注册Cap节点到consul
                var consulAdderss = new Uri(consulOption.ConsulUrl);
                var discoverOptions = serviceProvider.GetService<DiscoveryOptions>();
                var currenServerAddress = app.GetServiceAddress(consulOption);
                discoverOptions.DiscoveryServerHostName = consulAdderss.Host;
                discoverOptions.DiscoveryServerPort = consulAdderss.Port;
                discoverOptions.CurrentNodeHostName = currenServerAddress.Host;
                discoverOptions.CurrentNodePort = currenServerAddress.Port;
                discoverOptions.NodeId = currenServerAddress.Host.Replace(".", string.Empty) + currenServerAddress.Port;
                discoverOptions.NodeName = _serviceInfo.FullName.Replace("webapi", "cap");
                discoverOptions.MatchPath = $"/{_serviceInfo.ShortName}/cap";

                //注册本服务到consul
                app.RegisterToConsul(consulOption);
            }
        }
    }
}