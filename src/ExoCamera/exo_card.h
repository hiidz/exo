// The test card: what apps see whenever the DLL has no frame from the hub,
// so a camera that opens always shows something. Colour bars, so it is
// never mistaken for black, which is indistinguishable from a bug anywhere
// else in the chain.
#pragma once

#include "exo_format.h"

#include <windows.h>
#include <mfobjects.h>

// A new buffer of format.FrameBytes() holding the card in the advertised
// format, with its current length set. One reference, owned by the caller.
HRESULT ExoCreateCard(const ExoFormat& format, IMFMediaBuffer** out);
