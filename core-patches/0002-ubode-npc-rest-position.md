# ubODE: an NPC made standing still is thrown towards the region's corner

Patch: [`0002-ubode-npc-rest-position.patch`](0002-ubode-npc-rest-position.patch)
(against OpenSim master `9d00a5555225b17d3d1063cb0f3496fb1b6217f9`; one line in
`OpenSim/Region/PhysicsModules/ubOde/ODECharacter.cs`; apply with `git apply -p1`).

## Summary

With the ubODE physics engine, an NPC created with `osNpcCreate` (or a module's `INPCModule.CreateNPC`) on open
ground often jumps about a second later: its x and y are multiplied by about 0.9, so it lands some way off towards
the region's corner `<0, 0>` (about 27 m for one made at `<222, 168>`), and settles there. Its z barely changes.

## Cause

`ODECharacter.Move()` holds a standing avatar in place with a position controller: when the avatar has stopped,
`_zeroFlag` is set and the controller pulls the body back to `_zeroPosition`. `_zeroPosition` is recorded in the
"just stopped" branch (`if (!_zeroFlag) { _zeroFlag = true; _zeroPosition = _position; ... }`).

An NPC is made standing still, so ODE soon puts its body to sleep. `Move()` then returns early until
`m_bodydisablecontrol` reaches 50 (about a second), and wakes the body with:

```csharp
UBOdeNative.BodySetAngularVel(Body, 0f, 0f, 0f);
UBOdeNative.BodySetLinearVel(Body, 0f, 0f, 0f);
_zeroFlag = true;
UBOdeNative.BodyEnable(Body);
```

That sets `_zeroFlag` without `_zeroPosition`. For an avatar that never went through the "just stopped" branch,
`_zeroPosition` is still `Vector3.Zero`, so the controller pulls it towards `<0, 0, 0>`: one step moves it a fixed
fraction of the way (hence the scaling about the corner), and it stops once something resets the hold. Viewer
avatars rarely hit this because they arrive moving.

## The patch

Record the rest position where the sleeping body is woken:

```csharp
_zeroPosition = _position;
_zeroFlag = true;
```

`_position` is the body's position, kept up to date while it sleeps, so the avatar is held where it is.

## The add-on's workaround

Without the patch, `WebUserAvatars` (`SasWebUserAvatars.cs`) undoes an in-world move of an NPC in its first 5 s by
setting its position back (`AbsolutePosition`, which moves the existing body and re-arms the hold; `Teleport` would
make a new body with the same fault), instead of moving the NPC's voice. With the patch nothing moves, so the
workaround never acts.
