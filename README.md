# Installing

You need BepinEx 5 installed for Nuclear Option and [Muj's Command Mod](https://github.com/muji2498/CommandMod)

Just throw the dll into your plugins folder. Now whenever you are host, there will be a !whisper target message command available to everyone.

# Whispering to people with Spaces

Only the first argument of the command is used for the playername. It accepts a glob pattern with ? for any single character and * for any amount of characters.

So `Down ê Load    Pizza` can be written as `Down???Load*Pizza`. You could also just do `Down*` if you are sure no one else on the server starts with Down.
Names are case sensitive atm. This may change.

# Contributing

Just add your game folder in GameDir.targets and make sure you have the CommandMod in your bepin folder. Then it **should** dotnet build correctly.
