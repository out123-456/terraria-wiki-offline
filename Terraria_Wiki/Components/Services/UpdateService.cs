using System.Net.Http.Json;
using Terraria_Wiki.Models; // 替换为你的命名空间

namespace Terraria_Wiki.Services
{
    public class UpdateService
    {
        private readonly HttpClient _httpClient;

        // 定义两个仓库：原项目和你自己的 fork
        private const string UpstreamOwner = "BigBearKing";
        private const string UpstreamRepo = "terraria-wiki-offline";
        private const string MyOwner = "out123-456";
        private const string MyRepo = "terraria-wiki-offline";

        public UpdateService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TerrariaWikiScraper/1.0 (contact: bigbearkingus@gmail.com)");
        }

        public async Task<UpdateCheckResult?> CheckForUpdatesAsync()
        {
            App.AppStateManager.IsProcessing = true;
            try
            {
                // 1. 先检查原项目（BigBearKing）的最新 Release
                var upstreamRelease = await GetLatestReleaseAsync(UpstreamOwner, UpstreamRepo);
                if (upstreamRelease != null)
                {
                    string currentVersionStr = AppInfo.Current.VersionString;
                    string upstreamVersionStr = upstreamRelease.TagName.TrimStart('v', 'V');

                    if (Version.TryParse(currentVersionStr, out Version currentVersion) &&
                        Version.TryParse(upstreamVersionStr, out Version upstreamVersion))
                    {
                        if (upstreamVersion > currentVersion)
                        {
                            return new UpdateCheckResult
                            {
                                IsUpdateAvailable = true,
                                LatestVersion = upstreamVersionStr,
                                ReleaseNotes = upstreamRelease.ReleaseNotes,
                                DownloadUrl = upstreamRelease.ReleaseUrl,
                                Source = "原始项目 (BigBearKing)"
                            };
                        }
                    }
                }

                // 2. 如果原项目没有更新（或者获取失败），再检查你自己的 fork
                var myRelease = await GetLatestReleaseAsync(MyOwner, MyRepo);
                if (myRelease != null)
                {
                    string currentVersionStr = AppInfo.Current.VersionString;
                    string myVersionStr = myRelease.TagName.TrimStart('v', 'V');

                    if (Version.TryParse(currentVersionStr, out Version currentVersion) &&
                        Version.TryParse(myVersionStr, out Version myVersion))
                    {
                        if (myVersion > currentVersion)
                        {
                            return new UpdateCheckResult
                            {
                                IsUpdateAvailable = true,
                                LatestVersion = myVersionStr,
                                ReleaseNotes = myRelease.ReleaseNotes,
                                DownloadUrl = myRelease.ReleaseUrl,
                                Source = "当前使用项目 (out123-456)"
                            };
                        }
                    }
                }

                // 都没有更新
                return new UpdateCheckResult
                {
                    IsUpdateAvailable = false,
                    LatestVersion = AppInfo.Current.VersionString,
                    ReleaseNotes = null,
                    DownloadUrl = null,
                    Source = null
                };
            }
            catch (Exception ex)
            {
                // 记录日志或重新抛出
                throw;
            }
            finally
            {
                App.AppStateManager.IsProcessing = false;
            }
        }

        // 辅助方法：根据 owner/repo 获取最新的 Release 信息
        private async Task<GitHubReleaseInfo?> GetLatestReleaseAsync(string owner, string repo)
        {
            string apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
            try
            {
                var releaseInfo = await _httpClient.GetFromJsonAsync(apiUrl, AppJsonContext.Custom.GitHubReleaseInfo);
                return releaseInfo;
            }
            catch (HttpRequestException)
            {
                // 如果仓库不存在或者没有 Release，返回 null
                return null;
            }
        }
    }

    // 扩展 UpdateCheckResult 类，增加 Source 字段（可选）
    public class UpdateCheckResult
    {
        public bool IsUpdateAvailable { get; set; }
        public string? LatestVersion { get; set; }
        public string? ReleaseNotes { get; set; }
        public string? DownloadUrl { get; set; }
        public string? Source { get; set; }  // 标明更新来自哪个仓库
    }
}