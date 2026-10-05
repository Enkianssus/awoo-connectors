These reduced responses were captured from QQ's public `musicu.fcg` Desktop
search method on 2026-10-05 UTC. Requests used the normal system network,
`comm: { ct: 11, cv: 1003006, v: 1003006 }`, and no cookies or account credentials.
Only the response envelope and catalog fields needed by these tests remain.

- `desktop-search-public.json`: `晴天`, three results. The first song's ID,
  MID and type also matched the existing legacy search endpoint; its action
  switch preserves QQ's availability flag. A catalog result does not grant
  playback entitlement: playback remains subject to the logged-in QQ client.
- `desktop-search-empty-public.json`: a continuous random query with no
  matches, confirming that a successful empty response remains representable.

The prior `ct: 24, cv: 0` context returned code 0 with an empty list for the
known song `远航星的告别`, both over POST and GET. The supported context returned
that song on the same endpoint and method. This is evidence about the request
contract on the test network, not proof that another machine's WiFi is healthy.
