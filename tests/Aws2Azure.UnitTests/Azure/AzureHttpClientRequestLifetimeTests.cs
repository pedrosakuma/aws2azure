using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Buffers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aws2Azure.UnitTests.Azure;

public sealed class AzureHttpClientRequestLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Non_duplex_memory_content_does_not_finish_on_early_http2_headers(bool cancel)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var headersSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodyRead = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.Http2.InitialStreamWindowSize = 65535;
            options.Limits.Http2.InitialConnectionWindowSize = 65535;
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2);
        });
        await using var app = builder.Build();
        app.Run(async context =>
        {
            context.Response.StatusCode = 400;
            await context.Response.StartAsync(deadline.Token);
            headersSent.TrySetResult();
            await allowRead.Task.WaitAsync(deadline.Token);
            try
            {
                using var received = new MemoryStream();
                await context.Request.Body.CopyToAsync(received, deadline.Token);
                bodyRead.TrySetResult(received.ToArray());
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                bodyRead.TrySetCanceled();
            }
        });
        await app.StartAsync(deadline.Token);
        var address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses;
        var uri = new Uri(System.Linq.Enumerable.Single(address));
        using var transport = new AzureHttpClient(new ExactHttp2Handler(), ownsHandler: true,
            new AzureHttpClientOptions { MaxAttempts = 1 });
        using var owner = new PooledByteBufferWriter(256 * 1024);
        owner.GetSpan(256 * 1024)[..(256 * 1024)].Fill(0x61);
        owner.Advance(256 * 1024);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ReadOnlyMemoryContent(owner.WrittenMemory),
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var send = transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        try
        {
            await headersSent.Task.WaitAsync(deadline.Token);
            Assert.False(send.IsCompleted);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(deadline.Token));
            }
            else
            {
                allowRead.TrySetResult();
                using var response = await send.WaitAsync(deadline.Token);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal(HttpVersion.Version20, response.Version);
                var bytes = await bodyRead.Task.WaitAsync(deadline.Token);
                Assert.True(owner.WrittenMemory.Span.SequenceEqual(bytes), "Wire bytes changed during upload.");
            }
        }
        finally
        {
            allowRead.TrySetResult();
            await app.StopAsync(deadline.Token);
        }
    }

    private sealed class ExactHttp2Handler : DelegatingHandler
    {
        public ExactHttp2Handler() : base(new SocketsHttpHandler { UseProxy = false }) { }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            return base.SendAsync(request, ct);
        }
    }
}
