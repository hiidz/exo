// The test card: what apps see whenever the DLL has no frame from the hub
// (DECISIONS 5.2). Colour bars, so it is never mistaken for black, which is
// indistinguishable from a bug anywhere else in the chain.
#pragma once

#include <windows.h>
#include <mfobjects.h>

// A new buffer of EXO_FRAME_BYTES holding the card in the advertised
// format, with its current length set. One reference, owned by the caller.
HRESULT ExoCreateCard(IMFMediaBuffer** out);
