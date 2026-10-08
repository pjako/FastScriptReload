[![Documentation](https://img.shields.io/badge/docs-blue.svg)](https://fastscriptreload.com/projects/fast-script-reload/documentation)
[![Video Quick Start](https://img.shields.io/badge/video_tutorial-green.svg)](https://www.youtube.com/watch?v=ElzvL8M-tYw)
[![Discord](https://img.shields.io/discord/1054303923743227925?label=Chat)](https://discord.gg/wBKuEAsKAq)
[![License: MIT](https://img.shields.io/badge/Donate-red.svg)](https://www.patreon.com/FastScriptReload)


**Are you tired of waiting for full domain-reload and script compilation every time you make a small code change?**

Me too.

[![Fast Script Reload](_github~/fast-script-reload-workflow.gif)](https://immersivevrtools.com/projects/fast-script-reload?action=play_video "Watch Full Video")
[*Watch Full Video*](https://immersivevrtools.com/projects/fast-script-reload?action=play_video)

# Fast Script Reload
> Iterate on code insanely fast without breaking play session. Supports any editor. 1. Play 2. Make change 3. See results

Tool will automatically compile only what you've changed and immediately hot reload that into current play session.

Iterate on whatever you're working on without reentering play mode over and over again.

And you don't have to adjust your code either, just import.

Works with any code editor.

## Quickstart
1) Download [latest FSR version](https://github.com/pjako/FastScriptReload/releases/latest) and import to Unity
> You can also install via package manager. Window -> Package Manager -> + -> Add package from Git url:
> https://github.com/pjako/FastScriptReload.git?path=Assets

> Pulling a specific version, branch or commit can be done by appending it, eg https://github.com/pjako/FastScriptReload.git?path=Assets#2.0.0
3) Play
3) Make Code Change
4) See results

**It's that simple.**

[Quick start video created by Matt@SpeedTutor](https://www.youtube.com/watch?v=ElzvL8M-tYw)

# Unity Asset Store Best Seller
I was really chuffed to see FSR selling over 1000 copies on [Asset Store](https://assetstore.unity.com/packages/tools/utilities/fast-script-reload-239351?aid=1100ltZSe&pubref=github) in February - propelling it to #1 best-selling for a few days and keeping on first page for most of the month.

Gaining reviews like:
> **Goosebumps and tears of joy**
>
> Felt like I was underwater and that I can breathe again. Just buy this stuff for any serious project. Working around the limitations is just worth it.

## Hot-Reload on device / Live Script Reload
You can use same hot reload functionality in actual builds / on device. Iterate quickly on deployed Android APK / standalone windows build (exe).

> It's a standalone, paid extension asset, if you'd like to support the product - [please consider grabbing a copy on Unity Asset Store](https://assetstore.unity.com/packages/tools/utilities/live-script-reload-on-device-hot-reload-239380?aid=1100ltZSe&pubref=fsr-github-lsr)

## One-off custom code executions on Hot-Reload
When you need to set the stage to test your feature out.

Add following methods to changed script:

```
void OnScriptHotReload()
{
    //do whatever you want to do with access to instance via 'this'
}
```

```
static void OnScriptHotReloadNoInstance()
{
    //do whatever you want to do without instance
    //useful if you've added brand new type
    //or want to simply execute some code without |any instance created.
    //Like reload scene, call test function etc
}
```

## Performance

It's a development tool, you're not supposed to ship with it! :)
Your app performance won't be affected in any meaningful way though.
Biggest bit is additional memory used for your re-compiled code.
Won't be visible unless you make 100s of changes in same play-session.

## Supports (Tested)
- Windows / Mac (Intel and Apple Silicon) / Linux
- Unity 2022.3
- Unity 6

## Documentation
[Full documentation is available here](https://fastscriptreload.com/projects/fast-script-reload/documentation)

## Few things to have in mind, limitations:
* most limitations can be overcome with User Defined Script Overrides (see docs for more info)

### Generic methods and classes
Hot-Reloaded, with some exceptions for generic classes used with reference types (see docs).

### Creating new public methods
Hot-reload for new methods will only work with private methods (only called by changed code)

### Adding new fields
You can add new fields and tweak them in editor! Minor limitations:
- outside classes can not call new fields added at runtime
- new fields will only show in editor if they were already used (at least once)

### Extensive use of nested classed / structs
If your code-base contains lots of nested classes - you may see more compilation errors.

### Other minor limitations
There are some other minor limitations, please consult full list

## Roadmap
> Soon whole roadmap will be published as Github project for better visibility.

- more structured unit-tests, especially around script rewrite
- add Mac/Linux support - **(DONE, added with 1.1)**
- add debugger support for hot-reloaded scripts **(DONE, added with 1.2)**
- allow to add new fields (adjustable in Editor) **(DONE, added with 1.3)**
- editor mode support **(DONE, added with 1.4)**
- Apple Silicon support **(DONE, added with 2.0)**
- hot reload generic methods and classes **(DONE, added with 2.0)**
- better compiler support to workaround limitations


### FAQ
- My changes no longer automatically compile / reload
> Since 2.0 FSR starts Unity's compilation itself when scripts are saved outside play mode, and after play mode for scripts changed while playing.
>
> Older versions offered to disable Unity's auto refresh, you can turn it back on via `Edit -> Preferences -> Asset Pipeline -> Auto Refresh`.

- When importing I'm getting error: 'Unable to update following assemblies: (...)/ImmersiveVRTools.Common.Runtime.dll'
> This happens occasionally, especially on upgrade between versions. It's harmless error that'll go away on play mode.

- When upgrading between versions, eg 1.1 to 1.2 example scene cubes are pink
> This is down to reimporting 'Point' prefab. Right now plugin will make sure it's using correct shader eg. URP / Built-in but only on initial import.

To fix please go to `FastScripReload\Examples\Point\Point.prefab` and search for 'Point' shader.

### Building Hot Reload for Unity - blog posts about technical approach (with code)
I find FSR approach to hot reload very interesting (taking about being biased :)). I'm breaking down technical approach in a blog post series. You can find all about it here:

[1) Building hot reload functionality](https://immersivevrtools.com/Blog/how-to-build-hot-reload-functionality-for-unity) | [Download Example Project Code](_github~/building-hot-reload-for-unity-blog-posts-example-code/01-simple-approach.zip)

[2) Building hot reload for Unity builds / running directly on device](https://immersivevrtools.com/Blog/how-to-build-unity-hot-reload-on-device) | [Download Example Project Code](_github~/building-hot-reload-for-unity-blog-posts-example-code/02-hot-reload-on-device.zip)

## Credits & Thanks
- [Mono Mod](https://github.com/MonoMod/MonoMod) and [Harmony](https://github.com/pardeike/Harmony) - which provide runtime code detour that's at the core of hot reload approach.
- [CatlikeCoding](https://www.youtube.com/c/CatlikeCoding) - example scene code

# Support the project
If you got this far there's a good chance you're excited about FSR and want to help make it better.
That'd be really cool! There are few things you can do **that make huge difference.**

## Rave about FSR to other game devs!
Spread the word, let other devs know what it is, why is it cool and how it helps build your game.
I'm sure once they try it they'll stay with us!

## Help building it
Ping me on [Discord](https://discord.gg/wBKuEAsKAq) if you've got some time on your hands and would like to contribute!

Doesn't have to be coding - there's a ton of stuff that can be done.
Giving feedback on features, reporting issues, helping others  get started or just chatting and helping build FSR community. All that helps a lot!

## Become a patron
I'd love to continue spending most of my time making FSR better, as a indie dev however I need to balance that with paid client work. You can chip in to show your appreciation!

**[Support FSR by becoming a patron!](https://www.patreon.com/FastScriptReload)**

