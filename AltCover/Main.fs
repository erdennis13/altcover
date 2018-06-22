﻿namespace AltCover

module AltCover =
  let internal ToConsole () =
    Output.Error <- CommandLine.WriteErr
    Output.Usage <- CommandLine.Usage
    Output.Echo <- CommandLine.WriteErr
    Output.Info <- CommandLine.WriteOut
    Output.Warn <- CommandLine.WriteOut
    Output.Task <- false

  [<EntryPoint>]
  let private Main arguments =
    ToConsole()
    AltCover.Main.EffectiveMain arguments