# Pre-release 1.0 Patch Notes
## [Stable Release](https://github.com/tayjay/Talky/releases/latest) is the recommended version for 14.X.

With SCP:SL approaching its 15.0 release, I feel this is a good time to rewrite Talky to be more maintainable and build to a single DLL that will work in both Exiled and LabApi.
This pre-release works in the current SCP:SL version 14.2.7 for testing.

This version could still see a major overhaul if my [open suggestion](https://github.com/northwood-studios/LabAPI/issues/262) is approved.

### TL;DR
Talky is now a single DLL that works in both Exiled and LabApi plugin directories. 
You will need to remove the old Talky.LabAPI.dll and Talky.Exiled.dll from your plugins folder and copy any config changes from your old Talky configs to the new Talky config.
This update includes performance improvements, bug fixes, and preparations for changes coming in 15.0.

## Breaking Changes
- Change to single DLL build. 
  - Will require removing old Talky.LabAPI.dll and Talky.Exiled.dll from plugins folder
  - Will require copying any config changes from old Talky configs to new Talky config.
- Renamed plugin to Talky


## Changes
- Speaking logic applies to all roles to allow for all roles with human models to animate.
- Speech processing now accepts proximity, radio, and intercom voice channels instead of requiring HumanVoiceModule/IVoiceRole.
- Improve CedMod patching logic to target both Exiled and LabApi.
- Opus sample buffer changed to 4800, sample array changed to 480 to match packet sizes, and volume decoding cleanup
- Move compatibility checks to ServerEvents.PluginsEnabled to ensure all LabApi and Exiled assemblies are loaded before checking for them.
- Add HarmonyPresent() checks and null guards before applying/removing patches to avoid errors when Harmony is not present. Remove snapshot system and related code.
- Enable Talking setting changes now update SpeechTracker.ShouldAnimateFace rather than resetting the player emotion.
- Change the project build target to explicit x64 to hide build warnings.

## Fixes
- SpeechTracker.DefaultPreset is cached on init and updated when the player setting changes.
- Look optimization: fake look sync now early-returns when both current and previous look offsets are zero.
- Early return LookOverride if no offset is needed.
- clean up emotion override logic
- Emotion override timing now uses Time.time seconds instead of Unix milliseconds
- Glance/look cleanup: removed redundant checks
- Tidy up complaints from the linter