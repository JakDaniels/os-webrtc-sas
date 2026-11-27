// os-webrtc-sas: WebRTC voice for OpenSimulator using SpatialAudioServer.
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
//
// SAS room acoustics: sets how the voice room this object stands in sounds, from the "Acoustics" notecard in the
// object. Applied when rezzed, when the region starts and whenever the notecard changes. The owner touches the
// object and answers Yes to save the room's current settings to the notecard (replacing it), e.g. to start one.
//
// The notecard ("key = value" lines; # or // starts a comment):
//   room = parcel              which voice room: parcel (the parcel's own) or region (the region's)
//   acoustics = hall           outdoor, tiny-room, small-room, big-room, hall, long-hall, cave, cathedral
//   engine = acoustic          the server's mix engine (acoustics are heard only with "acoustic")
//   distance-curve = default   outdoor, meeting, or default (the server's)
// The engine and distance curve change when nobody is in the room (at once if it is empty).
// Any acoustic parameter too, heard at once, e.g. reverb-decay = 1.6, room-width = 12, floor-material = carpet,
// reflection-delays = 12, 20, 33; a key with no value goes back to the preset's. Touching and saving writes them all,
// the ones not set as # comments with the room's current values.
//
// Needs the os-webrtc-sas module, and [OSSL] AllowMODFunctions = true (OpenSim's default). A parcel's own room may be
// set by the parcel owner or an estate owner/manager; the region's room by estate owners/managers only. One object
// controls a room: while it is there, other objects are refused.

string NOTECARD = "Acoustics";
key applied; // the notecard version last applied, so saving it does not apply it again
integer channel; // the Yes/No dialog's
integer listener;

closeDialog()
{
    llListenRemove(listener);
    listener = 0;
    llSetTimerEvent(0);
}

save()
{
    string error = sasGetRoomParameters(NOTECARD);
    if (error == "")
    {
        applied = llGetInventoryKey(NOTECARD);
        llOwnerSay("Saved the voice room's settings to '" + NOTECARD + "'.");
    }
    else
    {
        llOwnerSay("Could not save the voice room's settings: " + error);
    }
}

apply()
{
    if (llGetInventoryType(NOTECARD) != INVENTORY_NOTECARD)
    {
        llOwnerSay("No '" + NOTECARD + "' notecard yet: touch me to save the voice room's current settings to one.");
        return;
    }
    key version = llGetInventoryKey(NOTECARD);
    if (version == applied && version != NULL_KEY)
    {
        return;
    }
    string error = sasSetRoomParameters(NOTECARD);
    if (error == "")
    {
        applied = version;
        llOwnerSay("Voice room settings applied from '" + NOTECARD + "'.");
    }
    else
    {
        llOwnerSay("Voice room settings not applied: " + error);
    }
}

default
{
    state_entry()
    {
        applied = NULL_KEY;
        apply();
    }

    on_rez(integer start)
    {
        llResetScript();
    }

    changed(integer change)
    {
        if (change & CHANGED_REGION_START)
        {
            applied = NULL_KEY;
            apply();
        }
        else if (change & CHANGED_INVENTORY)
        {
            apply();
        }
    }

    touch_start(integer count)
    {
        key owner = llGetOwner();
        if (llDetectedKey(0) != owner)
        {
            return;
        }
        closeDialog();
        channel = -1000000 - (integer)llFrand(1000000000);
        listener = llListen(channel, "", owner, "");
        string question = "Save the voice room's current settings to the '" + NOTECARD + "' notecard?";
        if (llGetInventoryType(NOTECARD) == INVENTORY_NOTECARD)
        {
            question += "\n\nThis replaces the notecard, and anything written in it.";
        }
        llDialog(owner, question, ["Yes", "No"], channel);
        llSetTimerEvent(60); // the question lapses unanswered
    }

    listen(integer ch, string name, key id, string answer)
    {
        closeDialog();
        if (answer == "Yes")
        {
            save();
        }
    }

    timer()
    {
        closeDialog();
    }
}
