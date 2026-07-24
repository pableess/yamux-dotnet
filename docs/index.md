---
title: Home
layout: simple
og_type: website
---

<section class="text-center py-5">
  <div class="container">
    <img src="{{site.basepath}}/img/yamux-dotnet.png" alt="Yamux .NET - Multiplexing for .NET" class="img-fluid" style="width: min(100%, 48rem); height: auto;">
    <p class="lead mt-4 mb-4">
      A high-performance .NET implementation of the <a href="https://github.com/hashicorp/yamux">HashiCorp Yamux multiplexing protocol</a>.<br>
      Multiple reliable, ordered, independent streams over a <strong>single</strong> underlying connection.
    </p>
    <div class="d-flex justify-content-center gap-3 mt-4 flex-wrap">
      <a href="{{site.basepath}}/docs/getting-started/" class="btn btn-primary btn-lg"><i class="bi bi-rocket-takeoff"></i> Getting Started</a>
      <a href="{{site.basepath}}/docs/Session/" class="btn btn-outline-secondary btn-lg"><i class="bi bi-book"></i> Documentation</a>
      <a href="https://github.com/pableess/yamux-dotnet" class="btn btn-info btn-lg"><i class="bi bi-github"></i> GitHub</a>
    </div>
    <div class="mt-4 text-start mx-auto" style="max-width: 48rem;">
      <pre class="language-shell-session"><code>dotnet add package Yamux</code></pre>
    </div>
  </div>
</section>

<section class="container my-5">
  <div class="row row-cols-1 row-cols-lg-2 gx-5 gy-4">
    <div class="col">
      <div class="card h-100">
        <div class="card-header display-6"><i class="bi bi-diagram-3"></i> Full-duplex multiplexing</div>
        <div class="card-body">
          <p class="card-text">
            Hundreds of logical streams over a single connection. Built on <code>System.IO.Pipelines</code> for high-performance I/O.
          </p>
          <a href="{{site.basepath}}/docs/Session/" class="btn btn-outline-primary btn-sm">Session docs</a>
        </div>
      </div>
    </div>
    <div class="col">
      <div class="card h-100">
        <div class="card-header display-6"><i class="bi bi-intersect"></i> Channel-based abstraction</div>
        <div class="card-body">
          <p class="card-text">
            <code>IDuplexSessionChannel</code>, <code>IReadOnlySessionChannel</code>, and <code>IWriteOnlySessionChannel</code> for flexible stream management.
          </p>
          <a href="{{site.basepath}}/docs/Channels/" class="btn btn-outline-primary btn-sm">Channel docs</a>
        </div>
      </div>
    </div>
    <div class="col">
      <div class="card h-100">
        <div class="card-header display-6"><i class="bi bi-sliders"></i> Configurable flow control</div>
        <div class="card-body">
          <p class="card-text">
            Automatic window tuning based on RTT. Configurable receive window sizes and upper bounds.
          </p>
          <a href="{{site.basepath}}/docs/SessionChannelOptions/" class="btn btn-outline-primary btn-sm">Options docs</a>
        </div>
      </div>
    </div>
    <div class="col">
      <div class="card h-100">
        <div class="card-header display-6"><i class="bi bi-bar-chart"></i> Bandwidth statistics</div>
        <div class="card-body">
          <p class="card-text">
            Per-session and per-channel bandwidth tracking with <code>Sampled</code> events and OpenTelemetry metrics integration.
          </p>
          <a href="{{site.basepath}}/docs/Statistics/" class="btn btn-outline-primary btn-sm">Statistics docs</a>
        </div>
      </div>
    </div>
    <div class="col">
      <div class="card h-100">
        <div class="card-header display-6"><i class="bi bi-hdd-network"></i> Pluggable transports</div>
        <div class="card-body">
          <p class="card-text">
            Support for <code>Stream</code>, <code>Socket</code>, <code>IDuplexPipe</code>, or custom <code>ITransport</code> implementations.
          </p>
          <a href="{{site.basepath}}/docs/Transport/" class="btn btn-outline-primary btn-sm">Transport docs</a>
        </div>
      </div>
    </div>
    <div class="col">
      <div class="card h-100">
        <div class="card-header display-6"><i class="bi bi-speedometer2"></i> High performance</div>
        <div class="card-body">
          <p class="card-text">
            Pooled value task sources, lock-free ID generation, AOT compatible, and native publish support.
          </p>
          <a href="{{site.basepath}}/docs/Performance/" class="btn btn-outline-primary btn-sm">Benchmarks</a>
        </div>
      </div>
    </div>
  </div>
</section>