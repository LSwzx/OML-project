using System.Text.Json;
using OML.Abstractions.Auth;
using OML.Abstractions.Downloads;
using OML.Abstractions.Models;

// 阶段 2 自检：验证模型能正确反序列化 Mojang 真实 JSON 结构，接口/工具类可编译调用。
// 任何一个断言失败都以非零退出码结束，方便后续接 CI。

var failures = new List<string>();
var passed = 0;

void Check(string name, bool condition)
{
    if (condition)
    {
        passed++;
        Console.WriteLine($"  [PASS] {name}");
    }
    else
    {
        failures.Add(name);
        Console.WriteLine($"  [FAIL] {name}");
    }
}

const string manifestJson = """
{
  "latest": { "release": "1.21.1", "snapshot": "24w33a" },
  "versions": [
    {
      "id": "1.21.1",
      "type": "release",
      "url": "https://piston-meta.mojang.com/v1/packages/abc/1.21.1.json",
      "time": "2024-08-08T11:06:32+00:00",
      "releaseTime": "2024-08-08T11:06:32+00:00",
      "sha1": "abc123",
      "complianceLevel": 1
    },
    {
      "id": "24w33a",
      "type": "snapshot",
      "url": "https://piston-meta.mojang.com/v1/packages/def/24w33a.json"
    }
  ]
}
""";

const string versionJson = """
{
  "id": "1.21.1",
  "type": "release",
  "mainClass": "net.minecraft.client.main.Main",
  "assets": "17",
  "assetIndex": {
    "id": "17",
    "sha1": "aaa111",
    "size": 410000,
    "totalSize": 527000000,
    "url": "https://piston-meta.mojang.com/v1/packages/aaa111/17.json"
  },
  "downloads": {
    "client": {
      "sha1": "bbb222",
      "size": 26000000,
      "url": "https://piston-data.mojang.com/v1/objects/bbb222/client.jar"
    }
  },
  "javaVersion": { "component": "java-runtime-delta", "majorVersion": 21 },
  "arguments": {
    "game": [
      "--username",
      "${auth_player_name}",
      {
        "rules": [ { "action": "allow", "features": { "is_demo_user": true } } ],
        "value": "--demo"
      }
    ],
    "jvm": [ "-Djava.library.path=${natives_directory}", "-cp", "${classpath}" ]
  },
  "libraries": [
    {
      "name": "org.lwjgl:lwjgl:3.3.3",
      "downloads": {
        "artifact": {
          "path": "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar",
          "sha1": "ccc333",
          "size": 723000,
          "url": "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar"
        },
        "classifiers": {
          "natives-windows": {
            "path": "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar",
            "sha1": "ddd444",
            "size": 150000,
            "url": "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar"
          }
        }
      }
    },
    {
      "name": "ca.weblite:java-objc-bridge:1.1",
      "rules": [ { "action": "allow", "os": { "name": "osx" } } ]
    },
    {
      "name": "net.java:jinput-platform:2.0.5:natives-windows@jar",
      "url": "https://libraries.minecraft.net/",
      "natives": { "windows": "natives-windows" },
      "extract": { "exclude": [ "META-INF/" ] }
    }
  ]
}
""";

Console.WriteLine("== 1. VersionManifest ==");
var manifest = JsonSerializer.Deserialize<VersionManifest>(manifestJson)!;

Check("Latest.Release == 1.21.1", manifest.Latest?.Release == "1.21.1");
Check("Latest.Snapshot == 24w33a", manifest.Latest?.Snapshot == "24w33a");
Check("Versions.Count == 2", manifest.Versions.Count == 2);
Check("Versions[0].Type == release", manifest.Versions[0].Type == "release");
Check("Versions[0].Sha1 反序列化", manifest.Versions[0].Sha1 == "abc123");
Check("Versions[0].ReleaseTime 反序列化", manifest.Versions[0].ReleaseTime is not null);
Check("Versions[1].Url 反序列化", manifest.Versions[1].Url?.EndsWith("24w33a.json") == true);

Console.WriteLine("== 2. VersionDetail ==");
var detail = JsonSerializer.Deserialize<VersionDetail>(versionJson)!;

Check("Id / Type", detail.Id == "1.21.1" && detail.Type == "release");
Check("MainClass", detail.MainClass == "net.minecraft.client.main.Main");
Check("Assets == 17", detail.Assets == "17");
Check("AssetIndex.TotalSize", detail.AssetIndex?.TotalSize == 527000000);
Check("Downloads.Client.Url", detail.Downloads?.Client?.Url?.EndsWith("client.jar") == true);
Check("JavaVersion.MajorVersion == 21", detail.JavaVersion?.MajorVersion == 21);
Check("Libraries.Count == 3", detail.Libraries.Count == 3);
Check("Game 参数 3 个", detail.Arguments?.Game.Count == 3);
Check("Game[2] 为条件对象", detail.Arguments?.Game[2].ValueKind == JsonValueKind.Object);
Check("Jvm 参数 3 个", detail.Arguments?.Jvm.Count == 3);

Console.WriteLine("== 3. Library ==");
var lwjgl = detail.Libraries[0];
Check("Artifact.Path", lwjgl.Downloads?.Artifact?.Path == "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar");
Check("Classifiers 命中 natives-windows",
    lwjgl.Downloads?.Classifiers?.TryGetValue("natives-windows", out var natives) == true &&
    natives.Path?.EndsWith("natives-windows.jar") == true);
Check("Rules[0].Os.Name == osx", detail.Libraries[1].Rules?[0].Os?.Name == "osx");
Check("Natives 字典", detail.Libraries[2].Natives?["windows"] == "natives-windows");
Check("Extract.Exclude", detail.Libraries[2].Extract?.Exclude.Count == 1);
Check("旧格式库可推导 Maven 路径",
    detail.Libraries[2].MavenRelativePath ==
    "net/java/jinput-platform/2.0.5/jinput-platform-2.0.5-natives-windows.jar");

Console.WriteLine("== 4. MavenCoordinate ==");
Check("三段坐标解析",
    MavenCoordinate.TryParse("org.lwjgl:lwjgl:3.3.3", out var c1) &&
    c1.GroupId == "org.lwjgl" && c1.ArtifactId == "lwjgl" &&
    c1.Version == "3.3.3" && c1.Classifier is null &&
    c1.RelativePath == "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar");
Check("四段 + @ext 解析",
    MavenCoordinate.TryParse("net.java:jinput-platform:2.0.5:natives-windows@jar", out var c2) &&
    c2.Classifier == "natives-windows" && c2.Extension == "jar");
Check("非法坐标被拒绝",
    !MavenCoordinate.TryParse("", out _) &&
    !MavenCoordinate.TryParse("bad:name", out _));

Console.WriteLine("== 5. Download ==");
Check("DownloadUrls.Combine 双侧斜杠归一",
    DownloadUrls.Combine("https://bmclapi2.bangbang93.com/", "/version.json") ==
    "https://bmclapi2.bangbang93.com/version.json");
Check("DownloadUrls.Rebase 换源",
    DownloadUrls.Rebase("https://piston-data.mojang.com/v1/objects/ab/client.jar",
        "https://bmclapi2.bangbang93.com/") ==
    "https://bmclapi2.bangbang93.com/v1/objects/ab/client.jar");

var task = new DownloadTask
{
    Url = "https://piston-data.mojang.com/v1/objects/ab/client.jar",
    SavePath = Path.Combine(Path.GetTempPath(), "client.jar"),
    ExpectedSize = 26000000,
    Sha1 = "bbb222"
};
Check("DownloadTask 构造与默认值",
    task.ExpectedSize == 26000000 && task.Sha1 == "bbb222");

DownloadSource[] sources =
[
    new() { Name = "Mojang", BaseUrl = "https://piston-meta.mojang.com/", IsOfficial = true, Priority = 10 },
    new() { Name = "BMCLAPI", BaseUrl = "https://bmclapi2.bangbang93.com/", Priority = 0 }
];
Check("DownloadSource 列表", sources.Length == 2 && sources[1].Priority == 0);

Console.WriteLine("== 6. Auth ==");
var profile = new UserProfile
{
    Username = "Steve",
    Uuid = "069a79f4-44e9-4726-a5be-fca90e38aaf5",
    AccessToken = null,
    SkinUrl = null
};
Check("UserProfile 默认类型为 Offline", profile.Type == AuthTypes.Offline);
Check("AuthTypes.All 含三种类型",
    AuthTypes.All.Count == 3 &&
    AuthTypes.All.Contains(AuthTypes.Microsoft) &&
    AuthTypes.All.Contains(AuthTypes.LittleSkin));

Console.WriteLine();
Console.WriteLine($"通过 {passed} 项，失败 {failures.Count} 项。");
if (failures.Count > 0)
{
    Console.WriteLine("失败项：");
    foreach (var f in failures)
    {
        Console.WriteLine($"  - {f}");
    }

    return 1;
}

return 0;
