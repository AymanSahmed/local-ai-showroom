// The project uses Microsoft.NET.Sdk + the ASP.NET Core shared framework (instead of the Web SDK)
// so it behaves like a plain console app for packaging. These are the usings the Web SDK would add.
global using System.Diagnostics;
global using Microsoft.AspNetCore.Builder;
global using Microsoft.AspNetCore.Hosting;
global using Microsoft.AspNetCore.Http;
global using Microsoft.AspNetCore.Routing;
global using Microsoft.Extensions.Configuration;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Logging;
