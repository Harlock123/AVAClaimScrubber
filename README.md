# AVA Claim Scrubber

Checks HIPAA 837 claims (professional and institutional, 5010) before they're sent, so they aren't rejected by the
clearinghouse (a 999 or 277CA) or denied by the payer (an 835). Each finding says what's wrong, where (the claim, its
loop and element, such as `2400 SV1-04`), why (the rule and where it comes from) and how to fix it.

*Work in progress: milestone 6 of 7 (the MCP server for AI agents, de-identified, and the AI Chat).*

## Running it

```bash
dotnet run --project src/AVAClaimScrubber -- samples/clinic-837p.x12
```

`samples/` has invented claims, each clean or with one planted problem named in its account number (CLM01).

## License

MIT
