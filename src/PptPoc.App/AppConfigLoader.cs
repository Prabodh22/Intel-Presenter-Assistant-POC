using Microsoft.Extensions.Configuration;
using PptPoc.Core.Configuration;

namespace PptPoc.App;

public static class AppConfigLoader
{
    public static AppConfig Load()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        var config = new AppConfig();
        configuration.GetSection("AppConfig").Bind(config);
        return config;
    }
}
