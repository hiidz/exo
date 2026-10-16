#include "exo_activate.h"
#include "exo_dll.h"
#include "exo_log.h"
#include "exo_source.h"

#include <mfapi.h>
#include <new>

using Microsoft::WRL::ComPtr;

ExoActivate::ExoActivate()
{
    InterlockedIncrement(&g_objects);
}

ExoActivate::~ExoActivate()
{
    InterlockedDecrement(&g_objects);
}

HRESULT ExoActivate::Create(ExoActivate** out)
{
    if (!out) {
        return E_POINTER;
    }
    *out = nullptr;
    ExoActivate* a = new (std::nothrow) ExoActivate();
    if (!a) {
        return E_OUTOFMEMORY;
    }
    HRESULT hr = MFCreateAttributes(&a->m_attrs, 4);
    if (FAILED(hr)) {
        a->Release();  // the only reference, so this deletes it
        return hr;
    }
    *out = a;
    return S_OK;
}

// ---- IUnknown -------------------------------------------------------

STDMETHODIMP ExoActivate::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) {
        return E_POINTER;
    }
    if (riid == IID_IUnknown || riid == IID_IMFActivate) {
        *ppv = static_cast<IMFActivate*>(this);
    } else if (riid == IID_IMFAttributes) {
        *ppv = static_cast<IMFAttributes*>(this);
    } else {
        // Logged because a miss logged this way is how it was found that
        // IMFActivate was wanted at all.
        *ppv = nullptr;
        ExoLogQiMiss("ExoActivate", riid);
        return E_NOINTERFACE;
    }
    AddRef();  // the caller owns the reference it was handed
    return S_OK;
}

STDMETHODIMP_(ULONG) ExoActivate::AddRef()
{
    return (ULONG)InterlockedIncrement(&m_ref);
}

STDMETHODIMP_(ULONG) ExoActivate::Release()
{
    LONG r = InterlockedDecrement(&m_ref);
    if (r == 0) {
        delete this;
    }
    return (ULONG)r;
}

// ---- IMFActivate ----------------------------------------------------

STDMETHODIMP ExoActivate::ActivateObject(REFIID riid, void** ppv)
{
    if (!ppv) {
        return E_POINTER;
    }
    *ppv = nullptr;

    // Created once and cached: a second ActivateObject must return the same
    // source, not a second camera. Creating it is the one call made under
    // the lock, because two racing callers must not both create one.
    // ExoSource::Create never calls back into this object, so it cannot
    // deadlock.
    HRESULT hr = S_OK;
    AcquireSRWLockExclusive(&m_lock);
    if (!m_source) {
        ExoSource* created = nullptr;
        hr = ExoSource::Create(&created);
        if (SUCCEEDED(hr)) {
            m_source.Attach(created);  // takes over Create's reference
        }
    }
    ComPtr<IMFMediaSource> source = m_source;
    ReleaseSRWLockExclusive(&m_lock);

    if (FAILED(hr)) {
        return hr;
    }
    // Outside the lock: never hold a lock across a call into another object.
    hr = source->QueryInterface(riid, ppv);
    ExoLog("ActivateObject hr=0x%08lX", (unsigned long)hr);
    return hr;
}

STDMETHODIMP ExoActivate::ShutdownObject()
{
    AcquireSRWLockExclusive(&m_lock);
    ComPtr<IMFMediaSource> source;
    source.Swap(m_source);  // take it out, leaving m_source empty
    ReleaseSRWLockExclusive(&m_lock);

    if (source) {
        source->Shutdown();
    }
    ExoLog("ExoActivate::ShutdownObject, had source=%d", source ? 1 : 0);
    return S_OK;
}

STDMETHODIMP ExoActivate::DetachObject()
{
    // Forget the source without shutting it down; the caller now owns it.
    AcquireSRWLockExclusive(&m_lock);
    m_source.Reset();
    ReleaseSRWLockExclusive(&m_lock);
    ExoLog("ExoActivate::DetachObject");
    return S_OK;
}

// ---- IMFAttributes, mechanical forwarding ---------------------------

STDMETHODIMP ExoActivate::GetItem(REFGUID k, PROPVARIANT* v)
{ return m_attrs->GetItem(k, v); }
STDMETHODIMP ExoActivate::GetItemType(REFGUID k, MF_ATTRIBUTE_TYPE* t)
{ return m_attrs->GetItemType(k, t); }
STDMETHODIMP ExoActivate::CompareItem(REFGUID k, REFPROPVARIANT v, BOOL* r)
{ return m_attrs->CompareItem(k, v, r); }
STDMETHODIMP ExoActivate::Compare(IMFAttributes* t, MF_ATTRIBUTES_MATCH_TYPE m, BOOL* r)
{ return m_attrs->Compare(t, m, r); }
STDMETHODIMP ExoActivate::GetUINT32(REFGUID k, UINT32* v)
{ return m_attrs->GetUINT32(k, v); }
STDMETHODIMP ExoActivate::GetUINT64(REFGUID k, UINT64* v)
{ return m_attrs->GetUINT64(k, v); }
STDMETHODIMP ExoActivate::GetDouble(REFGUID k, double* v)
{ return m_attrs->GetDouble(k, v); }
STDMETHODIMP ExoActivate::GetGUID(REFGUID k, GUID* v)
{ return m_attrs->GetGUID(k, v); }
STDMETHODIMP ExoActivate::GetStringLength(REFGUID k, UINT32* n)
{ return m_attrs->GetStringLength(k, n); }
STDMETHODIMP ExoActivate::GetString(REFGUID k, LPWSTR buf, UINT32 cch, UINT32* n)
{ return m_attrs->GetString(k, buf, cch, n); }
STDMETHODIMP ExoActivate::GetAllocatedString(REFGUID k, LPWSTR* buf, UINT32* n)
{ return m_attrs->GetAllocatedString(k, buf, n); }
STDMETHODIMP ExoActivate::GetBlobSize(REFGUID k, UINT32* n)
{ return m_attrs->GetBlobSize(k, n); }
STDMETHODIMP ExoActivate::GetBlob(REFGUID k, UINT8* buf, UINT32 cb, UINT32* n)
{ return m_attrs->GetBlob(k, buf, cb, n); }
STDMETHODIMP ExoActivate::GetAllocatedBlob(REFGUID k, UINT8** buf, UINT32* n)
{ return m_attrs->GetAllocatedBlob(k, buf, n); }
STDMETHODIMP ExoActivate::GetUnknown(REFGUID k, REFIID riid, LPVOID* ppv)
{ return m_attrs->GetUnknown(k, riid, ppv); }
STDMETHODIMP ExoActivate::SetItem(REFGUID k, REFPROPVARIANT v)
{ return m_attrs->SetItem(k, v); }
STDMETHODIMP ExoActivate::DeleteItem(REFGUID k)
{ return m_attrs->DeleteItem(k); }
STDMETHODIMP ExoActivate::DeleteAllItems()
{ return m_attrs->DeleteAllItems(); }
STDMETHODIMP ExoActivate::SetUINT32(REFGUID k, UINT32 v)
{ return m_attrs->SetUINT32(k, v); }
STDMETHODIMP ExoActivate::SetUINT64(REFGUID k, UINT64 v)
{ return m_attrs->SetUINT64(k, v); }
STDMETHODIMP ExoActivate::SetDouble(REFGUID k, double v)
{ return m_attrs->SetDouble(k, v); }
STDMETHODIMP ExoActivate::SetGUID(REFGUID k, REFGUID v)
{ return m_attrs->SetGUID(k, v); }
STDMETHODIMP ExoActivate::SetString(REFGUID k, LPCWSTR v)
{ return m_attrs->SetString(k, v); }
STDMETHODIMP ExoActivate::SetBlob(REFGUID k, const UINT8* buf, UINT32 cb)
{ return m_attrs->SetBlob(k, buf, cb); }
STDMETHODIMP ExoActivate::SetUnknown(REFGUID k, IUnknown* v)
{ return m_attrs->SetUnknown(k, v); }
STDMETHODIMP ExoActivate::LockStore()
{ return m_attrs->LockStore(); }
STDMETHODIMP ExoActivate::UnlockStore()
{ return m_attrs->UnlockStore(); }
STDMETHODIMP ExoActivate::GetCount(UINT32* n)
{ return m_attrs->GetCount(n); }
STDMETHODIMP ExoActivate::GetItemByIndex(UINT32 i, GUID* k, PROPVARIANT* v)
{ return m_attrs->GetItemByIndex(i, k, v); }
STDMETHODIMP ExoActivate::CopyAllItems(IMFAttributes* dest)
{ return m_attrs->CopyAllItems(dest); }
