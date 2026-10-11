// What the class factory hands out. MFCreateVirtualCamera's "CLSID of the
// custom media source to be activated" is literal: the pipeline creates the
// CLSID and asks it for IMFActivate, and ActivateObject returns the source
// (spike 1 FINDINGS, "The contract is IMFActivate, not IMFMediaSource").
//
// IMFActivate inherits IMFAttributes, so every attribute method is
// forwarded to a real store from MFCreateAttributes.
#pragma once

#include <windows.h>
#include <mfidl.h>
#include <mfobjects.h>
#include <wrl/client.h>

class ExoActivate : public IMFActivate
{
public:
    // Returns the object with one reference, owned by the caller.
    static HRESULT Create(ExoActivate** out);

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override;
    STDMETHODIMP_(ULONG) Release() override;

    // IMFActivate
    STDMETHODIMP ActivateObject(REFIID riid, void** ppv) override;
    STDMETHODIMP ShutdownObject() override;
    STDMETHODIMP DetachObject() override;

    // IMFAttributes, each forwarded to m_attrs
    STDMETHODIMP GetItem(REFGUID k, PROPVARIANT* v) override;
    STDMETHODIMP GetItemType(REFGUID k, MF_ATTRIBUTE_TYPE* t) override;
    STDMETHODIMP CompareItem(REFGUID k, REFPROPVARIANT v, BOOL* r) override;
    STDMETHODIMP Compare(IMFAttributes* t, MF_ATTRIBUTES_MATCH_TYPE m, BOOL* r) override;
    STDMETHODIMP GetUINT32(REFGUID k, UINT32* v) override;
    STDMETHODIMP GetUINT64(REFGUID k, UINT64* v) override;
    STDMETHODIMP GetDouble(REFGUID k, double* v) override;
    STDMETHODIMP GetGUID(REFGUID k, GUID* v) override;
    STDMETHODIMP GetStringLength(REFGUID k, UINT32* n) override;
    STDMETHODIMP GetString(REFGUID k, LPWSTR buf, UINT32 cch, UINT32* n) override;
    STDMETHODIMP GetAllocatedString(REFGUID k, LPWSTR* buf, UINT32* n) override;
    STDMETHODIMP GetBlobSize(REFGUID k, UINT32* n) override;
    STDMETHODIMP GetBlob(REFGUID k, UINT8* buf, UINT32 cb, UINT32* n) override;
    STDMETHODIMP GetAllocatedBlob(REFGUID k, UINT8** buf, UINT32* n) override;
    STDMETHODIMP GetUnknown(REFGUID k, REFIID riid, LPVOID* ppv) override;
    STDMETHODIMP SetItem(REFGUID k, REFPROPVARIANT v) override;
    STDMETHODIMP DeleteItem(REFGUID k) override;
    STDMETHODIMP DeleteAllItems() override;
    STDMETHODIMP SetUINT32(REFGUID k, UINT32 v) override;
    STDMETHODIMP SetUINT64(REFGUID k, UINT64 v) override;
    STDMETHODIMP SetDouble(REFGUID k, double v) override;
    STDMETHODIMP SetGUID(REFGUID k, REFGUID v) override;
    STDMETHODIMP SetString(REFGUID k, LPCWSTR v) override;
    STDMETHODIMP SetBlob(REFGUID k, const UINT8* buf, UINT32 cb) override;
    STDMETHODIMP SetUnknown(REFGUID k, IUnknown* v) override;
    STDMETHODIMP LockStore() override;
    STDMETHODIMP UnlockStore() override;
    STDMETHODIMP GetCount(UINT32* n) override;
    STDMETHODIMP GetItemByIndex(UINT32 i, GUID* k, PROPVARIANT* v) override;
    STDMETHODIMP CopyAllItems(IMFAttributes* dest) override;

private:
    // Private: only Create makes one, and only Release destroys one.
    ExoActivate();
    ~ExoActivate();

    LONG m_ref = 1;
    SRWLOCK m_lock = SRWLOCK_INIT;  // guards m_source
    Microsoft::WRL::ComPtr<IMFAttributes> m_attrs;
    Microsoft::WRL::ComPtr<IMFMediaSource> m_source;
};
