// IKsControl for a source with no kernel driver behind it. The Frame Server
// never calls Start, and never asks the stream for a sample, until both the
// source and the stream answer QueryInterface for IKsControl (spike 1
// FINDINGS, "IKsControl and IMFGetService are what unblock Start"). It does
// not need any property to work, so every request is refused with the
// conventional "no such property" answer.
//
// A mixin: it implements only the three KS methods. IUnknown comes from the
// class that inherits it, which owns the one refcount for the whole object.
#pragma once

#include <windows.h>
#include <ks.h>
#include <ksproxy.h>

// Declared in the SDK but defined by no import library we link, so spelled
// out here (spike 1).
static const GUID EXO_IID_IKsControl =
    { 0x28F54685, 0x06FD, 0x11D2, { 0xB2, 0x7A, 0x00, 0xA0, 0xC9, 0x22, 0x31, 0x96 } };

class ExoNoKsControl : public IKsControl
{
public:
    STDMETHODIMP KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG* returned) override
    {
        return Refuse(returned);
    }
    STDMETHODIMP KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG* returned) override
    {
        return Refuse(returned);
    }
    STDMETHODIMP KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG* returned) override
    {
        return Refuse(returned);
    }

private:
    static HRESULT Refuse(ULONG* returned)
    {
        if (returned) {
            *returned = 0;
        }
        return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
    }
};
