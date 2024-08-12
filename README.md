# SocksRelayServerExt
Fork of [OutisNemo/SocksRelayServer](https://github.com/OutisNemo/SocksRelayServer) 

## About the project

A simple SOCKS v4a proxy server written in .NET Standard 2.0 which forwards all traffic to a SOCKS v5 server. The proxy server does not support authentication however it can connect to a SOCKS v5 server using username and password. UDP and Bind commands are not supported. It also provides an interface for custom DNS resolving and a default DNS resolver as well. It can also use the upstream proxy for resolving the hostnames.

## New features
- Running on active thread instead of creating new one
- Error handling on local socket

## How to install
You can easily install this package to your project using NuGet.

See the [NuGet page](https://www.nuget.org/packages/OutisNemo.SocksRelayServer/)

## How to use
You can find detailed usage examples in the `tests/SocksRelayServerTests.csproj` project.

## How to write a custom DNS resolver
All you need to do is implement the `IDnsResolver` interface and pass your implementation to the `SocksRelayServer` instance using it's `DnsResolver` property. You can see a working example in the `tests/SocksRelayServerTests.cs` file.

## License
See the [LICENSE file](LICENSE.md) in this repository.
