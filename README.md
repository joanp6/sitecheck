# sitecheck

[![build](https://github.com/joanp6/sitecheck/actions/workflows/ci.yml/badge.svg)](https://github.com/joanp6/sitecheck/actions/workflows/ci.yml)

CLI that audits and monitors small-business websites: SSL, mobile, load time, broken links, form delivery

## Testing

`dotnet test` runs the unit suite. [docs/testing.md](docs/testing.md) covers the rest: the
xUnit v3 / Microsoft.Testing.Platform setup and why `global.json` is required for it, the
integration suite, and why the coverage floor is 70 % rather than 100 %.
