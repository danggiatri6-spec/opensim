# Avatar interaction session lifecycle notes

Date: 2026-11-24

## Invitation preservation on acceptance conflict

The acceptance path keeps a valid invitation pending until the server can atomically create its interaction session. Previously, the invitation was removed before checking whether either participant already had an active session or the region had reached its session capacity; a conflict therefore consumed the invitation even though no session was created.

The revised flow:

- Validates the invitation and participant role under the invitation lock.
- Rejects and cancellations consume the invitation immediately.
- Acceptance re-checks requester presence and range. If the requester is unavailable or out of range, the invitation is removed and the requester is notified.
- Before creating a session, acceptance re-checks that the invitation still exists under the lock, then checks session capacity and participant conflicts.
- A capacity or participant conflict leaves the invitation available for a later valid attempt.
- Successful session creation consumes the invitation atomically.
- A concurrent second accept/cancel cannot create a duplicate session because invitation removal and session insertion are serialized by the same lock.

This change does not alter capability fields, event names, invitation expiry, session lifetime, or Viewer protocol.

## Verification

The committed diff was reviewed for invitation removal and session insertion paths. No C# build or simulator runtime test has been run.
