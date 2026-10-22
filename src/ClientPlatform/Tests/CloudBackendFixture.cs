using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// 跨仓库契约测试的“真实后端”fixture：把私有后端仓库的 Cloud.Api
/// 作为子进程拉起（InMemory 存储，随机端口）。客户端测试不引用后端项目（避免跨仓库
/// 构建耦合），后端只在测试运行时需要，位置可配置、找不到即跳过。
///
/// 后端位置解析顺序：
///   1. 环境变量 <c>MYTHOLOGY_CLOUD_BACKEND</c>（指向含 Mythology.Cloud.Api.csproj 的目录）；
///   2. 从测试程序集向上找到客户端仓库根，探测兄弟目录（私有后端仓库的 backend）。
/// </summary>
public sealed class CloudBackendFixture : IAsyncLifetime
{
    /// <summary>后端基址（未找到后端时为 null，测试应 Assert.Skip）。</summary>
    public string? BaseUrl { get; private set; }

    /// <summary>后端是否成功拉起；false 时测试跳过并给出定位提示。</summary>
    public bool Available { get; private set; }

    private const int TailLines = 12;

    private Process? _process;
    private readonly Queue<string> _stdoutTail = new();
    private readonly Queue<string> _stderrTail = new();
    private readonly object _tailLock = new();

    public async ValueTask InitializeAsync()
    {
        var dir = ResolveBackendDir();
        if (dir is null)
        {
            Available = false;
            return;
        }

        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--no-launch-profile");
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["Cloud__DevelopmentApiKey"] = "mk_test_dev-local_contract";
        psi.Environment["ASPNETCORE_URLS"] = BaseUrl;
        // OAuth 本地回调契约：客户端 MYTHOLOGY_WEB_URL 与后端 Cloud:WebPublicUrl 必须指向
        // 同一前端 origin（断言 redirect_uri == http://localhost:3000/callback）。
        psi.Environment["Cloud__WebPublicUrl"] = "http://localhost:3000";

        _process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动后端进程。");

        // 必须持续排空重定向管道：Serilog 控制台每请求一行，管道缓冲写满后后端进程会阻塞在
        // Console 写（表现为任意请求永久挂起、进程仍存活——曾使 device/approve 在契约测试中
        // 挂到 HttpClient 100s 超时）。按行异步读取并只保留尾部若干行用于失败诊断。
        _process.OutputDataReceived += (_, e) => AppendTail(_stdoutTail, e.Data);
        _process.ErrorDataReceived += (_, e) => AppendTail(_stderrTail, e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        // 等待就绪：任一 HTTP 响应（含 404/500）都算“已在监听”，连接拒绝才算未就绪。
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(120);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_process.HasExited)
                throw new InvalidOperationException($"后端进程提前退出：{ReadTail()}");
            try
            {
                using var resp = await http.GetAsync($"{BaseUrl}/health/ready");
                Available = true;
                return;
            }
            catch
            {
                await Task.Delay(500);
            }
        }
        throw new InvalidOperationException($"后端启动超时（120s）：{ReadTail()}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not null && !_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 已退出
            }
            await _process.WaitForExitAsync();
        }
        _process?.Dispose();
    }

    private string ReadTail()
    {
        string stdout;
        string stderr;
        lock (_tailLock)
        {
            stdout = string.Join(Environment.NewLine, _stdoutTail);
            stderr = string.Join(Environment.NewLine, _stderrTail);
        }
        return $"[stdout]\n{stdout}\n[stderr]\n{stderr}";
    }

    private void AppendTail(Queue<string> tail, string? line)
    {
        if (line is null)
            return;
        lock (_tailLock)
        {
            tail.Enqueue(line);
            while (tail.Count > TailLines)
                tail.Dequeue();
        }
    }

    private static int FreePort()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    private static string? ResolveBackendDir()
    {
        var env = Environment.GetEnvironmentVariable("MYTHOLOGY_CLOUD_BACKEND");
        if (!string.IsNullOrWhiteSpace(env))
            return Directory.Exists(env) ? Path.GetFullPath(env) : null;

        // 从测试程序集向上找客户端仓库根（含 src/Mythology.slnx），探测兄弟私有仓库
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (!File.Exists(Path.Combine(d.FullName, "src", "Mythology.slnx")))
                continue;
            var sibling = Path.GetFullPath(
                Path.Combine(d.FullName, "..", "MythologyCloud", "backend", "Mythology.Cloud.Api")
            );
            return Directory.Exists(sibling) ? sibling : null;
        }
        return null;
    }
}

/// <summary>
/// 共享同一真实后端子进程的测试集合：消费类（AuthContractTests / RetrievalContractTests 等）
/// 顺序复用同一 fixture 实例——避免并发 <c>dotnet run</c> 争抢同一后端项目构建，
/// 也省一次后端冷启动。
/// </summary>
[CollectionDefinition("live-backend")]
public sealed class LiveBackendCollection : ICollectionFixture<CloudBackendFixture> { }
