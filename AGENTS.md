Never synchronously block on async work (including `.GetAwaiter().GetResult()`, `.Result`, or task `.Wait()`); propagate async to the caller instead.
