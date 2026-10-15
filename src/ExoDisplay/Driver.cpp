/*++

Copyright (c) Microsoft Corporation

Abstract:

	This module contains a sample implementation of an indirect display driver.

	MSDN documentation on indirect displays can be found at https://msdn.microsoft.com/en-us/library/windows/hardware/mt761968(v=vs.85).aspx.

Environment:

	User Mode, UMDF

--*/

// ExoDisplay, Exo's virtual display adapter: Microsoft's IndirectDisplay
// sample (MS-PL, see LICENSE) by way of SudoVDA (MIT and CC0), cut down to
// what Exo needs. At most one monitor, plugged and unplugged by the hub
// through exo_display.h, offering exactly the modes the hub names, SDR only.

#include "Driver.h"
#include "edid.h"
#include "exo_log.h"

#include <sddl.h>
#include <stdio.h>

#include <algorithm>
#include <atomic>
#include <list>
#include <mutex>
#include <thread>

using namespace std;
using namespace Microsoft::IndirectDisp;
using namespace Microsoft::WRL;

// One display at a time (CLAUDE.md, out of scope), so connector 0 always.
static const UINT MaxMonitors = 1;

// opLock serialises plugging and unplugging: the IOCTL handler and the
// watchdog. listLock guards monitorCtxList only, and is never held across
// an IddCx call, because IddCx calls back into the parse callback during
// arrival and may destroy a monitor during departure.
static std::mutex opLock;
static std::mutex listLock;
static std::list<IndirectMonitorContext*> monitorCtxList;

static std::atomic<int> watchdogCountdown = 0;
static std::atomic<bool> stopping = false;
static std::thread watchdogThread;

static LUID preferredAdapterLuid{};
static bool preferredAdapterChanged = false;

// What IOCTL_EXO_GET_FRAME returns, written by the swap-chain thread.
static std::mutex frameLock;
static ExoFrameInfo frameInfo{};
static UINT frameGeneration = 0;  // under frameLock

// The shared texture's DACL: SYSTEM, LOCAL SERVICE (this host), and any
// signed-in user, so the unelevated hub in the user's session can open it.
static const wchar_t* SharedTextureSddl = L"D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;AU)";
// The cursor event's: a signed-in user may only wait on it (SYNCHRONIZE).
static const wchar_t* CursorEventSddl = L"D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;0x00100000;;;AU)";

// What IOCTL_EXO_GET_CURSOR returns, written by the monitor's cursor thread.
static std::mutex cursorLock;
static ExoCursorInfo cursorInfo{};
static std::vector<BYTE> cursorShape;  // under cursorLock
static HANDLE cursorEvent = nullptr;   // EXO_CURSOR_EVENT, for the driver's lifetime

#pragma region helpers

static void FillSignalInfo(DISPLAYCONFIG_VIDEO_SIGNAL_INFO& Mode, const ExoMode& m, bool bMonitorMode)
{
	Mode.totalSize.cx = Mode.activeSize.cx = m.Width;
	Mode.totalSize.cy = Mode.activeSize.cy = m.Height;

	// See https://docs.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_video_signal_info
	Mode.AdditionalSignalInfo.vSyncFreqDivider = bMonitorMode ? 0 : 1;
	Mode.AdditionalSignalInfo.videoStandard = 255;

	Mode.vSyncFreq.Numerator = m.Hz;
	Mode.vSyncFreq.Denominator = 1;
	Mode.hSyncFreq.Numerator = m.Hz * m.Height;
	Mode.hSyncFreq.Denominator = 1;

	Mode.scanLineOrdering = DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE;

	Mode.pixelRate = ((UINT64)m.Hz) * ((UINT64)m.Width) * ((UINT64)m.Height);
}

static IDDCX_MONITOR_MODE CreateIddCxMonitorMode(const ExoMode& m, IDDCX_MONITOR_MODE_ORIGIN Origin)
{
	IDDCX_MONITOR_MODE Mode = {};

	Mode.Size = sizeof(Mode);
	Mode.Origin = Origin;
	FillSignalInfo(Mode.MonitorVideoSignalInfo, m, true);

	return Mode;
}

static IDDCX_TARGET_MODE CreateIddCxTargetMode(const ExoMode& m)
{
	IDDCX_TARGET_MODE Mode = {};

	Mode.Size = sizeof(Mode);
	FillSignalInfo(Mode.TargetVideoSignalInfo.targetVideoSignalInfo, m, false);

	return Mode;
}

#pragma endregion

extern "C" DRIVER_INITIALIZE DriverEntry;

EVT_WDF_DRIVER_UNLOAD ExoDriverUnload;
EVT_WDF_DRIVER_DEVICE_ADD ExoDeviceAdd;
EVT_WDF_DEVICE_D0_ENTRY ExoDeviceD0Entry;

EVT_IDD_CX_DEVICE_IO_CONTROL ExoIoDeviceControl;
EVT_IDD_CX_ADAPTER_INIT_FINISHED ExoAdapterInitFinished;
EVT_IDD_CX_ADAPTER_COMMIT_MODES ExoAdapterCommitModes;

EVT_IDD_CX_PARSE_MONITOR_DESCRIPTION ExoParseMonitorDescription;
EVT_IDD_CX_MONITOR_GET_DEFAULT_DESCRIPTION_MODES ExoMonitorGetDefaultModes;
EVT_IDD_CX_MONITOR_QUERY_TARGET_MODES ExoMonitorQueryModes;

EVT_IDD_CX_MONITOR_ASSIGN_SWAPCHAIN ExoMonitorAssignSwapChain;
EVT_IDD_CX_MONITOR_UNASSIGN_SWAPCHAIN ExoMonitorUnassignSwapChain;

struct IndirectDeviceContextWrapper
{
	IndirectDeviceContext* pContext;

	void Cleanup()
	{
		delete pContext;
		pContext = nullptr;
	}
};

struct IndirectMonitorContextWrapper
{
	IndirectMonitorContext* pContext;

	void Cleanup()
	{
		delete pContext;
		pContext = nullptr;
	}
};

// This macro creates the methods for accessing an IndirectDeviceContextWrapper as a context for a WDF object
WDF_DECLARE_CONTEXT_TYPE(IndirectDeviceContextWrapper);
WDF_DECLARE_CONTEXT_TYPE(IndirectMonitorContextWrapper);

extern "C" BOOL WINAPI DllMain(
	_In_ HINSTANCE hInstance,
	_In_ UINT dwReason,
	_In_opt_ LPVOID lpReserved)
{
	UNREFERENCED_PARAMETER(hInstance);
	UNREFERENCED_PARAMETER(lpReserved);

	if (dwReason == DLL_PROCESS_DETACH) {
		ExoLogClose();
	}
	return TRUE;
}

#pragma region monitor list

// A plugged monitor with this Id, not already departing.
static IndirectMonitorContext* FindMonitor(const GUID& id)
{
	std::lock_guard<std::mutex> lg(listLock);
	for (auto* ctx : monitorCtxList) {
		if (!ctx->departing && ctx->id == id) {
			return ctx;
		}
	}
	return nullptr;
}

static UINT LiveMonitorCount()
{
	std::lock_guard<std::mutex> lg(listLock);
	UINT n = 0;
	for (auto* ctx : monitorCtxList) {
		if (!ctx->departing) {
			n++;
		}
	}
	return n;
}

// With opLock held. Each monitor is marked departing under listLock, so no
// lookup finds it again, then departs outside it.
static void Unplug(const GUID* id)
{
	std::vector<IDDCX_MONITOR> monitors;
	{
		std::lock_guard<std::mutex> lg(listLock);
		for (auto* ctx : monitorCtxList) {
			if (!ctx->departing && (!id || ctx->id == *id)) {
				ctx->departing = true;
				monitors.push_back(ctx->GetMonitor());
			}
		}
	}
	for (auto monitor : monitors) {
		IddCxMonitorDeparture(monitor);
	}
}

// Unplugs every monitor when no IOCTL has come for ExoWatchdogSeconds.
static void RunWatchdog()
{
	watchdogThread = std::thread([] {
		while (!stopping) {
			Sleep(1000);
			if (LiveMonitorCount() == 0 || watchdogCountdown <= 0) {
				continue;
			}
			if (--watchdogCountdown == 0) {
				ExoLog("watchdog: no IOCTL for %u s, unplugging", ExoWatchdogSeconds);
				std::lock_guard<std::mutex> op(opLock);
				Unplug(nullptr);
			}
		}
	});
}

#pragma endregion

_Use_decl_annotations_
extern "C" NTSTATUS DriverEntry(
	PDRIVER_OBJECT  pDriverObject,
	PUNICODE_STRING pRegistryPath
)
{
	WDF_DRIVER_CONFIG Config;
	NTSTATUS Status;

	WDF_OBJECT_ATTRIBUTES Attributes;
	WDF_OBJECT_ATTRIBUTES_INIT(&Attributes);

	WDF_DRIVER_CONFIG_INIT(&Config,
		ExoDeviceAdd
	);

	Config.EvtDriverUnload = ExoDriverUnload;

	ExoLog("ExoDisplay loaded, protocol %u", ExoDisplayVersion);

	Status = WdfDriverCreate(pDriverObject, pRegistryPath, &Attributes, &Config, WDF_NO_HANDLE);
	if (!NT_SUCCESS(Status))
	{
		ExoLog("WdfDriverCreate failed 0x%08X", Status);
		return Status;
	}

	RunWatchdog();

	PSECURITY_DESCRIPTOR sd = nullptr;
	if (ConvertStringSecurityDescriptorToSecurityDescriptorW(CursorEventSddl, SDDL_REVISION_1, &sd, nullptr))
	{
		SECURITY_ATTRIBUTES sa = { sizeof(sa), sd, FALSE };
		cursorEvent = CreateEventW(&sa, FALSE, FALSE, EXO_CURSOR_EVENT);
		LocalFree(sd);
	}
	if (!cursorEvent)
	{
		ExoLog("cursor event %ls: create failed %lu", EXO_CURSOR_EVENT, GetLastError());
	}

	// The host process is this driver's alone (DeviceGroupId in the INF).
	SetPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS);

	return Status;
}

_Use_decl_annotations_
void ExoDriverUnload(_In_ WDFDRIVER) {
	stopping = true;
	watchdogThread.join();
}

_Use_decl_annotations_
NTSTATUS ExoDeviceAdd(WDFDRIVER Driver, PWDFDEVICE_INIT pDeviceInit)
{
	NTSTATUS Status = STATUS_SUCCESS;
	WDF_PNPPOWER_EVENT_CALLBACKS PnpPowerCallbacks;

	UNREFERENCED_PARAMETER(Driver);

	// Register for power callbacks - in this sample only power-on is needed
	WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&PnpPowerCallbacks);
	PnpPowerCallbacks.EvtDeviceD0Entry = ExoDeviceD0Entry;
	WdfDeviceInitSetPnpPowerEventCallbacks(pDeviceInit, &PnpPowerCallbacks);

	IDD_CX_CLIENT_CONFIG IddConfig;
	IDD_CX_CLIENT_CONFIG_INIT(&IddConfig);

	// If the driver wishes to handle custom IoDeviceControl requests, it's necessary to use this callback since IddCx
	// redirects IoDeviceControl requests to an internal queue.
	IddConfig.EvtIddCxDeviceIoControl = ExoIoDeviceControl;

	IddConfig.EvtIddCxAdapterInitFinished = ExoAdapterInitFinished;

	IddConfig.EvtIddCxParseMonitorDescription = ExoParseMonitorDescription;
	IddConfig.EvtIddCxMonitorGetDefaultDescriptionModes = ExoMonitorGetDefaultModes;
	IddConfig.EvtIddCxMonitorQueryTargetModes = ExoMonitorQueryModes;
	IddConfig.EvtIddCxAdapterCommitModes = ExoAdapterCommitModes;
	IddConfig.EvtIddCxMonitorAssignSwapChain = ExoMonitorAssignSwapChain;
	IddConfig.EvtIddCxMonitorUnassignSwapChain = ExoMonitorUnassignSwapChain;

	Status = IddCxDeviceInitConfig(pDeviceInit, &IddConfig);
	if (!NT_SUCCESS(Status))
	{
		return Status;
	}

	WDF_OBJECT_ATTRIBUTES Attr;
	WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&Attr, IndirectDeviceContextWrapper);
	Attr.EvtCleanupCallback = [](WDFOBJECT Object)
	{
		// Automatically cleanup the context when the WDF object is about to be deleted
		auto* pContext = WdfObjectGet_IndirectDeviceContextWrapper(Object);
		if (pContext)
		{
			pContext->Cleanup();
		}
	};

	WDFDEVICE Device = nullptr;
	Status = WdfDeviceCreate(&pDeviceInit, &Attr, &Device);
	if (!NT_SUCCESS(Status))
	{
		return Status;
	}

	// The hub finds the adapter by this interface.
	Status = WdfDeviceCreateDeviceInterface(Device, &ExoDisplayInterface, NULL);
	if (!NT_SUCCESS(Status))
	{
		return Status;
	}

	Status = IddCxDeviceInitialize(Device);

	// Create a new device context object and attach it to the WDF device object
	auto* pContext = WdfObjectGet_IndirectDeviceContextWrapper(Device);
	pContext->pContext = new IndirectDeviceContext(Device);

	return Status;
}

_Use_decl_annotations_
NTSTATUS ExoDeviceD0Entry(WDFDEVICE Device, WDF_POWER_DEVICE_STATE PreviousState)
{
	UNREFERENCED_PARAMETER(PreviousState);

	// This function is called by WDF to start the device in the fully-on power state.

	auto* pContext = WdfObjectGet_IndirectDeviceContextWrapper(Device);
	pContext->pContext->InitAdapter();

	return STATUS_SUCCESS;
}

#pragma region Direct3DDevice

Direct3DDevice::Direct3DDevice(LUID AdapterLuid) : AdapterLuid(AdapterLuid)
{
}

HRESULT Direct3DDevice::Init()
{
	// The DXGI factory could be cached, but if a new render adapter appears on the system, a new factory needs to be
	// created. If caching is desired, check DxgiFactory->IsCurrent() each time and recreate the factory if !IsCurrent.
	HRESULT hr = CreateDXGIFactory2(0, IID_PPV_ARGS(&DxgiFactory));
	if (FAILED(hr))
	{
		return hr;
	}

	// Find the specified render adapter
	hr = DxgiFactory->EnumAdapterByLuid(AdapterLuid, IID_PPV_ARGS(&Adapter));
	if (FAILED(hr))
	{
		return hr;
	}

	// Create a D3D device using the render adapter. BGRA support is required by the WHQL test suite.
	hr = D3D11CreateDevice(Adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, &Device, nullptr, &DeviceContext);
	if (FAILED(hr))
	{
		// If creating the D3D device failed, it's possible the render GPU was lost (e.g. detachable GPU) or else the
		// system is in a transient state.
		return hr;
	}

	return S_OK;
}

#pragma endregion

#pragma region SwapChainProcessor

SwapChainProcessor::SwapChainProcessor(IDDCX_SWAPCHAIN hSwapChain, shared_ptr<Direct3DDevice> Device, HANDLE NewFrameEvent)
	: m_hSwapChain(hSwapChain), m_Device(Device), m_hAvailableBufferEvent(NewFrameEvent)
{
	m_hTerminateEvent.Attach(CreateEvent(nullptr, FALSE, FALSE, nullptr));

	// Immediately create and run the swap-chain processing thread, passing 'this' as the thread parameter
	m_hThread.Attach(CreateThread(nullptr, 0, RunThread, this, 0, nullptr));
}

SwapChainProcessor::~SwapChainProcessor()
{
	// Alert the swap-chain processing thread to terminate
	SetEvent(m_hTerminateEvent.Get());

	if (m_hThread.Get())
	{
		// Wait for the thread to terminate
		WaitForSingleObject(m_hThread.Get(), INFINITE);
	}

	DropShared();
}

DWORD CALLBACK SwapChainProcessor::RunThread(LPVOID Argument)
{
	reinterpret_cast<SwapChainProcessor*>(Argument)->Run();
	return 0;
}

void SwapChainProcessor::Run()
{
	// For improved performance, make use of the Multimedia Class Scheduler Service, which will intelligently
	// prioritize this thread for improved throughput in high CPU-load scenarios.
	DWORD AvTask = 0;
	HANDLE AvTaskHandle = AvSetMmThreadCharacteristicsW(L"DisplayPostProcessing", &AvTask);

	RunCore();

	// The kept surface belongs to the swap-chain, so it goes first.
	m_Last.Reset();
	ExoLog("swap-chain done: %u images copied, %u found the hub holding the texture", m_Copied, m_Waited);

	// Always delete the swap-chain object when swap-chain processing loop terminates in order to kick the system to
	// provide a new swap-chain if necessary.
	WdfObjectDelete((WDFOBJECT)m_hSwapChain);
	m_hSwapChain = nullptr;

	AvRevertMmThreadCharacteristics(AvTaskHandle);
}

void SwapChainProcessor::RunCore()
{
	// Get the DXGI device interface
	ComPtr<IDXGIDevice> DxgiDevice;
	HRESULT hr = m_Device->Device.As(&DxgiDevice);
	if (FAILED(hr))
	{
		return;
	}

	IDARG_IN_SWAPCHAINSETDEVICE SetDevice = {};
	SetDevice.pDevice = DxgiDevice.Get();

	hr = IddCxSwapChainSetDevice(m_hSwapChain, &SetDevice);
	if (FAILED(hr))
	{
		ExoLog("IddCxSwapChainSetDevice failed 0x%08X", hr);
		return;
	}

	// Acquire and release buffers in a loop
	for (;;)
	{
		ComPtr<IDXGIResource> AcquiredBuffer;

		// Ask for the next buffer from the producer
		IDARG_OUT_RELEASEANDACQUIREBUFFER Buffer = {};
		hr = IddCxSwapChainReleaseAndAcquireBuffer(m_hSwapChain, &Buffer);

		// AcquireBuffer immediately returns STATUS_PENDING if no buffer is yet available
		if (hr == E_PENDING)
		{
			// We must wait for a new buffer
			HANDLE WaitHandles [] =
			{
				m_hAvailableBufferEvent,
				m_hTerminateEvent.Get()
			};
			DWORD WaitResult = WaitForMultipleObjects(ARRAYSIZE(WaitHandles), WaitHandles, FALSE, 16);
			if (WaitResult == WAIT_OBJECT_0 || WaitResult == WAIT_TIMEOUT)
			{
				// An image the hub was holding the texture against goes
				// in now, if the hub has let go.
				if (m_Pending)
				{
					Share();
				}
				// We have a new buffer, so try the AcquireBuffer again
				continue;
			}
			else if (WaitResult == WAIT_OBJECT_0 + 1)
			{
				// We need to terminate
				break;
			}
			else
			{
				// The wait was cancelled or something unexpected happened
				hr = HRESULT_FROM_WIN32(WaitResult);
				break;
			}
		}
		else if (SUCCEEDED(hr))
		{
			// We have new frame to process, the surface has a reference on it that the driver has to release
			AcquiredBuffer.Attach(Buffer.MetaData.pSurface);

			// The previous surface went back to the OS with this acquire,
			// so the newest one is always the one kept. Windows presents
			// an unchanged desktop again for a while (no dirty rects, no
			// moves, the same frame number); that image is already shared.
			// The very first image is new whatever its counts say.
			m_Last.Reset();
			AcquiredBuffer.As(&m_Last);
			bool changed = !m_HaveImage || Buffer.MetaData.DirtyRectCount != 0 || Buffer.MetaData.MoveRegionCount != 0;
			if (changed && m_Last)
			{
				m_HaveImage = true;
				m_LastFrameNumber = Buffer.MetaData.PresentationFrameNumber;
				m_LastPresentQpc = Buffer.MetaData.PresentDisplayQPCTime;
				m_Pending = true;
				m_Waiting = false;
			}
			if (m_Pending)
			{
				Share();
			}

			// Our reference is m_Last's, released at the next acquire. The
			// driver owns the surface until then, so a copy the hub
			// delayed can still read it.
			AcquiredBuffer.Reset();

			// Indicate to OS that we have finished inital processing of the frame, it is a hint that
			// OS could start preparing another frame
			hr = IddCxSwapChainFinishedProcessingFrame(m_hSwapChain);
			if (FAILED(hr))
			{
				break;
			}
		}
		else
		{
			// The swap-chain was likely abandoned (e.g. DXGI_ERROR_ACCESS_LOST), so exit the processing loop
			ExoLog("swap-chain acquire failed 0x%08X", hr);
			break;
		}
	}
}

// The shared texture, made to match the image and published for the hub.
// Remade when the image's size or format changes, under a new name, so the
// hub never reads a texture of the wrong shape.
bool SwapChainProcessor::EnsureShared()
{
	D3D11_TEXTURE2D_DESC image;
	m_Last->GetDesc(&image);
	if (m_Shared)
	{
		D3D11_TEXTURE2D_DESC have;
		m_Shared->GetDesc(&have);
		if (have.Width == image.Width && have.Height == image.Height && have.Format == image.Format)
		{
			return true;
		}
		DropShared();
	}

	D3D11_TEXTURE2D_DESC desc = {};
	desc.Width = image.Width;
	desc.Height = image.Height;
	desc.MipLevels = 1;
	desc.ArraySize = 1;
	desc.Format = image.Format;
	desc.SampleDesc.Count = 1;
	desc.Usage = D3D11_USAGE_DEFAULT;
	// The hub's video processor reads it, which needs both (traps-display).
	desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
	desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;

	ComPtr<ID3D11Texture2D> texture;
	HRESULT hr = m_Device->Device->CreateTexture2D(&desc, nullptr, &texture);
	ComPtr<IDXGIResource1> resource;
	ComPtr<IDXGIKeyedMutex> mutex;
	if (SUCCEEDED(hr)) hr = texture.As(&resource);
	if (SUCCEEDED(hr)) hr = texture.As(&mutex);
	if (FAILED(hr))
	{
		ExoLog("shared texture %ux%u format %u: create failed 0x%08X", image.Width, image.Height, image.Format, hr);
		return false;
	}

	UINT generation;
	{
		std::lock_guard<std::mutex> lg(frameLock);
		generation = ++frameGeneration;
	}
	wchar_t name[64];
	swprintf_s(name, L"Global\\ExoDisplayFrame%u", generation);

	PSECURITY_DESCRIPTOR sd = nullptr;
	if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(SharedTextureSddl, SDDL_REVISION_1, &sd, nullptr))
	{
		ExoLog("shared texture: security descriptor failed %lu", GetLastError());
		return false;
	}
	SECURITY_ATTRIBUTES sa = { sizeof(sa), sd, FALSE };
	HANDLE handle = nullptr;
	hr = resource->CreateSharedHandle(&sa, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, name, &handle);
	LocalFree(sd);
	if (FAILED(hr))
	{
		ExoLog("shared texture %ls: CreateSharedHandle failed 0x%08X", name, hr);
		return false;
	}

	m_Shared = texture;
	m_Mutex = mutex;
	m_SharedHandle.Attach(handle);
	{
		std::lock_guard<std::mutex> lg(frameLock);
		frameInfo = {};
		frameInfo.Generation = generation;
		frameInfo.RenderAdapter = m_Device->AdapterLuid;
		frameInfo.Width = image.Width;
		frameInfo.Height = image.Height;
		frameInfo.Format = image.Format;
		wcscpy_s(frameInfo.Name, name);
	}
	ExoLog("shared texture %ls: %ux%u format %u, render adapter %08X:%08X", name, image.Width, image.Height,
		image.Format, m_Device->AdapterLuid.HighPart, m_Device->AdapterLuid.LowPart);
	return true;
}

// Copies the kept image into the shared texture, unless the hub holds it.
// Never waits: this thread runs Windows' presentation of the monitor.
void SwapChainProcessor::Share()
{
	if (!EnsureShared())
	{
		m_Pending = false;
		return;
	}

	HRESULT hr = m_Mutex->AcquireSync(0, 0);
	if (hr == static_cast<HRESULT>(WAIT_TIMEOUT))
	{
		// Released with key 1: the hub has not taken the last image. This
		// one replaces it.
		hr = m_Mutex->AcquireSync(1, 0);
	}
	if (hr == static_cast<HRESULT>(WAIT_TIMEOUT))
	{
		// The hub holds it. The image stays pending, for the next wake.
		if (!m_Waiting)
		{
			m_Waiting = true;
			m_Waited++;
		}
		return;
	}
	if (hr != S_OK)
	{
		// WAIT_ABANDONED: the hub died holding it, and the texture is no
		// longer consistent. A new one, under a new name, next time.
		ExoLog("shared texture: AcquireSync returned 0x%08X, remaking it", hr);
		DropShared();
		return;
	}

	m_Device->DeviceContext->CopyResource(m_Shared.Get(), m_Last.Get());
	{
		std::lock_guard<std::mutex> lg(frameLock);
		frameInfo.FrameNumber = m_LastFrameNumber;
		frameInfo.PresentQpc = m_LastPresentQpc;
		frameInfo.Copied = ++m_Copied;
		frameInfo.Waited = m_Waited;
	}
	m_Mutex->ReleaseSync(1);
	m_Pending = false;
	m_Waiting = false;
}

void SwapChainProcessor::DropShared()
{
	if (!m_Shared)
	{
		return;
	}
	m_Mutex.Reset();
	m_Shared.Reset();
	m_SharedHandle.Close();
	std::lock_guard<std::mutex> lg(frameLock);
	frameInfo = {};
}

#pragma endregion

#pragma region IndirectDeviceContext

IndirectDeviceContext::IndirectDeviceContext(_In_ WDFDEVICE WdfDevice) :
	m_WdfDevice(WdfDevice)
{
	m_Adapter = {};
}

IndirectDeviceContext::~IndirectDeviceContext()
{
}

void IndirectDeviceContext::InitAdapter()
{
	IDDCX_ADAPTER_CAPS AdapterCaps = {};
	AdapterCaps.Size = sizeof(AdapterCaps);

	// Declare basic feature support for the adapter (required)
	AdapterCaps.MaxMonitorsSupported = MaxMonitors;
	AdapterCaps.EndPointDiagnostics.Size = sizeof(AdapterCaps.EndPointDiagnostics);
	AdapterCaps.EndPointDiagnostics.GammaSupport = IDDCX_FEATURE_IMPLEMENTATION_NONE;
	AdapterCaps.EndPointDiagnostics.TransmissionType = IDDCX_TRANSMISSION_TYPE_WIRED_OTHER;

	// Declare your device strings for telemetry (required)
	AdapterCaps.EndPointDiagnostics.pEndPointFriendlyName = L"Exo Display Adapter";
	AdapterCaps.EndPointDiagnostics.pEndPointManufacturerName = L"Exo";
	AdapterCaps.EndPointDiagnostics.pEndPointModelName = L"ExoDisplay";

	// Declare your hardware and firmware versions (required)
	IDDCX_ENDPOINT_VERSION Version = {};
	Version.Size = sizeof(Version);
	Version.MajorVer = 1;
	AdapterCaps.EndPointDiagnostics.pFirmwareVersion = &Version;
	AdapterCaps.EndPointDiagnostics.pHardwareVersion = &Version;

	// Initialize a WDF context that can store a pointer to the device context object
	WDF_OBJECT_ATTRIBUTES Attr;
	WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&Attr, IndirectDeviceContextWrapper);

	IDARG_IN_ADAPTER_INIT AdapterInit = {};
	AdapterInit.WdfDevice = m_WdfDevice;
	AdapterInit.pCaps = &AdapterCaps;
	AdapterInit.ObjectAttributes = &Attr;

	// Start the initialization of the adapter, which will trigger the AdapterFinishInit callback later
	IDARG_OUT_ADAPTER_INIT AdapterInitOut;
	NTSTATUS Status = IddCxAdapterInitAsync(&AdapterInit, &AdapterInitOut);

	if (NT_SUCCESS(Status))
	{
		// Store a reference to the WDF adapter handle
		m_Adapter = AdapterInitOut.AdapterObject;

		// Store the device context object into the WDF object context
		auto* pContext = WdfObjectGet_IndirectDeviceContextWrapper(AdapterInitOut.AdapterObject);
		pContext->pContext = this;
	}
}

void IndirectDeviceContext::SetRenderAdapter(const LUID& AdapterLuid) {
	IDARG_IN_ADAPTERSETRENDERADAPTER inArgs{AdapterLuid};
	IddCxAdapterSetRenderAdapter(m_Adapter, &inArgs);
}

// With opLock held.
NTSTATUS IndirectDeviceContext::CreateMonitor(const ExoAddMonitor& params, IndirectMonitorContext*& pMonitorContext)
{
	WDF_OBJECT_ATTRIBUTES Attr;
	WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&Attr, IndirectMonitorContextWrapper);
	Attr.EvtCleanupCallback = [](WDFOBJECT Object)
	{
		auto* pContext = WdfObjectGet_IndirectMonitorContextWrapper(Object);
		if (pContext)
		{
			pContext->Cleanup();
		}
	};

	BYTE edid[128];
	MakeEdid(edid, params);

	IDDCX_MONITOR_INFO MonitorInfo = {};
	MonitorInfo.Size = sizeof(MonitorInfo);
	MonitorInfo.MonitorType = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI;
	MonitorInfo.ConnectorIndex = 0;

	MonitorInfo.MonitorDescription.Size = sizeof(MonitorInfo.MonitorDescription);
	MonitorInfo.MonitorDescription.Type = IDDCX_MONITOR_DESCRIPTION_TYPE_EDID;
	MonitorInfo.MonitorDescription.DataSize = sizeof(edid);
	MonitorInfo.MonitorDescription.pData = edid;
	MonitorInfo.MonitorContainerId = params.Id;

	IDARG_IN_MONITORCREATE MonitorCreate = {};
	MonitorCreate.ObjectAttributes = &Attr;
	MonitorCreate.pMonitorInfo = &MonitorInfo;

	IDARG_OUT_MONITORCREATE MonitorCreateOut;
	NTSTATUS Status = IddCxMonitorCreate(m_Adapter, &MonitorCreate, &MonitorCreateOut);
	if (!NT_SUCCESS(Status))
	{
		return Status;
	}

	// Filled in before it joins the list, which the parse callback reads
	// during arrival.
	auto* pContext = new IndirectMonitorContext(MonitorCreateOut.MonitorObject);
	WdfObjectGet_IndirectMonitorContextWrapper(MonitorCreateOut.MonitorObject)->pContext = pContext;
	pContext->id = params.Id;
	memcpy(pContext->edid, edid, sizeof(edid));
	pContext->modes.assign(params.Modes, params.Modes + params.ModeCount);
	{
		std::lock_guard<std::mutex> lg(listLock);
		monitorCtxList.push_back(pContext);
	}

	// Tell the OS that the monitor has been plugged in
	IDARG_OUT_MONITORARRIVAL ArrivalOut;
	Status = IddCxMonitorArrival(MonitorCreateOut.MonitorObject, &ArrivalOut);
	if (!NT_SUCCESS(Status))
	{
		// Its cleanup callback deletes the context, which leaves the list.
		WdfObjectDelete(MonitorCreateOut.MonitorObject);
		return Status;
	}

	pContext->adapterLuid = ArrivalOut.OsAdapterLuid;
	pContext->targetId = ArrivalOut.OsTargetId;
	pMonitorContext = pContext;
	return STATUS_SUCCESS;
}

IndirectMonitorContext::IndirectMonitorContext(_In_ IDDCX_MONITOR Monitor) :
	m_Monitor(Monitor)
{
	m_hCursorEvent.Attach(CreateEvent(nullptr, FALSE, FALSE, nullptr));
	m_hCursorStop.Attach(CreateEvent(nullptr, TRUE, FALSE, nullptr));
}

IndirectMonitorContext::~IndirectMonitorContext()
{
	StopCursor();
	m_ProcessingThread.reset();

	std::lock_guard<std::mutex> lg(listLock);
	monitorCtxList.remove(this);
}

IDDCX_MONITOR IndirectMonitorContext::GetMonitor() const {
	return m_Monitor;
}

void IndirectMonitorContext::AssignSwapChain(const IDDCX_SWAPCHAIN& SwapChain, const LUID& RenderAdapter, const HANDLE& NewFrameEvent)
{
	StopCursor();
	m_ProcessingThread.reset();

	auto Device = make_shared<Direct3DDevice>(RenderAdapter);
	if (FAILED(Device->Init()))
	{
		// It's important to delete the swap-chain if D3D initialization fails, so that the OS knows to generate a new
		// swap-chain and try again.
		WdfObjectDelete(SwapChain);
		return;
	}

	// Create a new swap-chain processing thread
	m_ProcessingThread.reset(new SwapChainProcessor(SwapChain, Device, NewFrameEvent));

	// A hardware cursor, so Windows leaves the pointer out of the frames and
	// signals m_hCursorEvent when it changes, for the hub to forward. A
	// pointer larger than this falls back to being drawn into the frames.
	IDDCX_CURSOR_CAPS caps = {};
	caps.Size = sizeof(caps);
	caps.ColorXorCursorSupport = IDDCX_XOR_CURSOR_SUPPORT_FULL;
	caps.AlphaCursorSupport = true;
	caps.MaxX = EXO_CURSOR_MAX;
	caps.MaxY = EXO_CURSOR_MAX;

	IDARG_IN_SETUP_HWCURSOR hwCursor = {};
	hwCursor.CursorInfo = caps;
	hwCursor.hNewCursorDataAvailable = m_hCursorEvent.Get();

	NTSTATUS Status = IddCxMonitorSetupHardwareCursor(m_Monitor, &hwCursor);
	if (!NT_SUCCESS(Status))
	{
		ExoLog("hardware cursor setup failed 0x%08X", Status);
		return;
	}
	StartCursor();
}

void IndirectMonitorContext::UnassignSwapChain()
{
	StopCursor();
	// Stop processing the last swap-chain
	m_ProcessingThread.reset();
}

void IndirectMonitorContext::StartCursor()
{
	ResetEvent(m_hCursorStop.Get());
	m_CursorThread = std::thread([this] { RunCursor(); });
}

// Also hides the pointer for the hub, since this monitor no longer has it.
void IndirectMonitorContext::StopCursor()
{
	if (!m_CursorThread.joinable())
	{
		return;
	}
	SetEvent(m_hCursorStop.Get());
	m_CursorThread.join();
	{
		std::lock_guard<std::mutex> lg(cursorLock);
		cursorInfo.Seq++;
		cursorInfo.Visible = 0;
	}
	if (cursorEvent)
	{
		SetEvent(cursorEvent);
	}
}

// Reads the pointer each time Windows signals a change, publishes it for
// IOCTL_EXO_GET_CURSOR, and wakes the hub.
void IndirectMonitorContext::RunCursor()
{
	std::vector<BYTE> buffer(EXO_CURSOR_MAX * EXO_CURSOR_MAX * 4);
	// Not a shape Windows has, so the first query copies the current one.
	DWORD lastShapeId = 0xFFFFFFFF;
	bool failing = false;
	HANDLE waits[] = { m_hCursorEvent.Get(), m_hCursorStop.Get() };
	while (WaitForMultipleObjects(ARRAYSIZE(waits), waits, FALSE, INFINITE) == WAIT_OBJECT_0)
	{
		IDARG_IN_QUERY_HWCURSOR in = {};
		in.LastShapeId = lastShapeId;
		in.ShapeBufferSizeInBytes = (UINT)buffer.size();
		in.pShapeBuffer = buffer.data();
		IDARG_OUT_QUERY_HWCURSOR out = {};
		NTSTATUS Status = IddCxMonitorQueryHardwareCursor(m_Monitor, &in, &out);
		if (!NT_SUCCESS(Status))
		{
			if (!failing)
			{
				ExoLog("cursor query failed 0x%08X", Status);
			}
			failing = true;
			continue;
		}
		failing = false;

		LARGE_INTEGER now;
		QueryPerformanceCounter(&now);
		{
			std::lock_guard<std::mutex> lg(cursorLock);
			cursorInfo.Seq++;
			cursorInfo.Visible = out.IsCursorVisible;
			cursorInfo.X = out.X;
			cursorInfo.Y = out.Y;
			cursorInfo.Qpc = now.QuadPart;
			if (out.IsCursorShapeUpdated)
			{
				auto& s = out.CursorShapeInfo;
				lastShapeId = s.ShapeId;
				cursorInfo.ShapeId = s.ShapeId;
				cursorInfo.Type = s.CursorType;
				cursorInfo.Width = s.Width;
				cursorInfo.Height = s.Height;
				cursorInfo.Pitch = s.Pitch;
				cursorInfo.XHot = s.XHot;
				cursorInfo.YHot = s.YHot;
				size_t bytes = std::min((size_t)s.Pitch * s.Height, buffer.size());
				cursorShape.assign(buffer.begin(), buffer.begin() + bytes);
			}
		}
		if (cursorEvent)
		{
			SetEvent(cursorEvent);
		}
	}
}

#pragma endregion

#pragma region DDI Callbacks

_Use_decl_annotations_
NTSTATUS ExoAdapterInitFinished(IDDCX_ADAPTER AdapterObject, const IDARG_IN_ADAPTER_INIT_FINISHED* pInArgs)
{
	UNREFERENCED_PARAMETER(AdapterObject);

	return pInArgs->AdapterInitStatus;
}

_Use_decl_annotations_
NTSTATUS ExoAdapterCommitModes(IDDCX_ADAPTER AdapterObject, const IDARG_IN_COMMITMODES* pInArgs)
{
	UNREFERENCED_PARAMETER(AdapterObject);
	UNREFERENCED_PARAMETER(pInArgs);

	// Nothing to reconfigure: the swap-chain is taken care of by IddCx.
	return STATUS_SUCCESS;
}

// Called without the monitor object, during arrival and later, so the
// monitor is found by its EDID.
_Use_decl_annotations_
NTSTATUS ExoParseMonitorDescription(const IDARG_IN_PARSEMONITORDESCRIPTION* pInArgs, IDARG_OUT_PARSEMONITORDESCRIPTION* pOutArgs)
{
	if (pInArgs->MonitorDescription.DataSize != sizeof(IndirectMonitorContext::edid))
		return STATUS_INVALID_PARAMETER;

	std::vector<ExoMode> modes;
	{
		std::lock_guard<std::mutex> lg(listLock);
		for (auto* ctx : monitorCtxList) {
			if (!ctx->departing && memcmp(pInArgs->MonitorDescription.pData, ctx->edid, sizeof(ctx->edid)) == 0) {
				modes = ctx->modes;
				break;
			}
		}
	}
	if (modes.empty())
		return STATUS_INVALID_PARAMETER;

	pOutArgs->MonitorModeBufferOutputCount = (UINT)modes.size();
	pOutArgs->PreferredMonitorModeIdx = 0;

	if (pInArgs->MonitorModeBufferInputCount < modes.size())
	{
		// Return success if there was no buffer, since the caller was only asking for a count of modes
		return (pInArgs->MonitorModeBufferInputCount > 0) ? STATUS_BUFFER_TOO_SMALL : STATUS_SUCCESS;
	}

	for (size_t i = 0; i < modes.size(); i++) {
		pInArgs->pMonitorModes[i] = CreateIddCxMonitorMode(modes[i], IDDCX_MONITOR_MODE_ORIGIN_MONITORDESCRIPTOR);
	}
	return STATUS_SUCCESS;
}

// Only for a monitor without an EDID, which Exo never plugs; answered from
// the monitor's own modes all the same.
_Use_decl_annotations_
NTSTATUS ExoMonitorGetDefaultModes(IDDCX_MONITOR MonitorObject, const IDARG_IN_GETDEFAULTDESCRIPTIONMODES* pInArgs, IDARG_OUT_GETDEFAULTDESCRIPTIONMODES* pOutArgs)
{
	auto& modes = WdfObjectGet_IndirectMonitorContextWrapper(MonitorObject)->pContext->modes;

	pOutArgs->DefaultMonitorModeBufferOutputCount = (UINT)modes.size();
	pOutArgs->PreferredMonitorModeIdx = 0;

	if (pInArgs->DefaultMonitorModeBufferInputCount == 0) {
		return STATUS_SUCCESS;
	} else if (pInArgs->DefaultMonitorModeBufferInputCount < modes.size()) {
		return STATUS_BUFFER_TOO_SMALL;
	}

	for (size_t i = 0; i < modes.size(); i++) {
		pInArgs->pDefaultMonitorModes[i] = CreateIddCxMonitorMode(modes[i], IDDCX_MONITOR_MODE_ORIGIN_DRIVER);
	}
	return STATUS_SUCCESS;
}

// The modes the adapter can scan out to this monitor: the same list, so the
// intersection Windows offers is exactly what the hub asked for.
_Use_decl_annotations_
NTSTATUS ExoMonitorQueryModes(IDDCX_MONITOR MonitorObject, const IDARG_IN_QUERYTARGETMODES* pInArgs, IDARG_OUT_QUERYTARGETMODES* pOutArgs)
{
	auto& modes = WdfObjectGet_IndirectMonitorContextWrapper(MonitorObject)->pContext->modes;

	pOutArgs->TargetModeBufferOutputCount = (UINT)modes.size();

	if (pInArgs->TargetModeBufferInputCount >= modes.size()) {
		for (size_t i = 0; i < modes.size(); i++) {
			pInArgs->pTargetModes[i] = CreateIddCxTargetMode(modes[i]);
		}
	} else if (pInArgs->TargetModeBufferInputCount != 0) {
		return STATUS_BUFFER_TOO_SMALL;
	}

	return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS ExoMonitorAssignSwapChain(IDDCX_MONITOR MonitorObject, const IDARG_IN_SETSWAPCHAIN* pInArgs)
{
	auto* pMonitorContextWrapper = WdfObjectGet_IndirectMonitorContextWrapper(MonitorObject);

	// A render adapter the hub asked for that this swap-chain is not on:
	// abandon it, and the OS makes a new one on the new adapter.
	if (preferredAdapterChanged) {
		if (memcmp(&pInArgs->RenderAdapterLuid, &preferredAdapterLuid, sizeof(LUID))) {
			return STATUS_GRAPHICS_INDIRECT_DISPLAY_ABANDON_SWAPCHAIN;
		}
		preferredAdapterChanged = false;
	}

	pMonitorContextWrapper->pContext->AssignSwapChain(pInArgs->hSwapChain, pInArgs->RenderAdapterLuid, pInArgs->hNextSurfaceAvailable);
	return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS ExoMonitorUnassignSwapChain(IDDCX_MONITOR MonitorObject)
{
	auto* pMonitorContextWrapper = WdfObjectGet_IndirectMonitorContextWrapper(MonitorObject);
	pMonitorContextWrapper->pContext->UnassignSwapChain();
	return STATUS_SUCCESS;
}

#pragma endregion

_Use_decl_annotations_
VOID ExoIoDeviceControl(
	WDFDEVICE Device,
	WDFREQUEST Request,
	size_t OutputBufferLength,
	size_t InputBufferLength,
	ULONG IoControlCode
)
{
	UNREFERENCED_PARAMETER(OutputBufferLength);
	UNREFERENCED_PARAMETER(InputBufferLength);

	watchdogCountdown = ExoWatchdogSeconds;

	NTSTATUS Status = STATUS_INVALID_DEVICE_REQUEST;
	size_t bytesReturned = 0;
	auto* pDevice = WdfObjectGet_IndirectDeviceContextWrapper(Device)->pContext;

	std::lock_guard<std::mutex> op(opLock);

	switch (IoControlCode) {
	case IOCTL_EXO_ADD_MONITOR: {
		ExoAddMonitor* in;
		ExoAddMonitorOut* out;
		Status = WdfRequestRetrieveInputBuffer(Request, sizeof(*in), (PVOID*)&in, NULL);
		if (!NT_SUCCESS(Status)) {
			break;
		}
		Status = WdfRequestRetrieveOutputBuffer(Request, sizeof(*out), (PVOID*)&out, NULL);
		if (!NT_SUCCESS(Status)) {
			break;
		}

		if (in->ModeCount == 0 || in->ModeCount > EXO_MAX_MODES) {
			Status = STATUS_INVALID_PARAMETER;
			break;
		}
		for (UINT i = 0; i < in->ModeCount; i++) {
			if (in->Modes[i].Width == 0 || in->Modes[i].Height == 0 || in->Modes[i].Hz == 0) {
				Status = STATUS_INVALID_PARAMETER;
			}
		}
		if (!NT_SUCCESS(Status)) {
			break;
		}

		IndirectMonitorContext* ctx = FindMonitor(in->Id);
		if (!ctx) {
			if (LiveMonitorCount() >= MaxMonitors) {
				Status = STATUS_TOO_MANY_NODES;
				break;
			}
			Status = pDevice->CreateMonitor(*in, ctx);
			if (!NT_SUCCESS(Status)) {
				ExoLog("plug %.13s failed 0x%08X", in->Serial, Status);
				break;
			}
			ExoLog("plugged %.13s: %ux%u, %u modes, target %u", in->Serial, in->Modes[0].Width, in->Modes[0].Height,
				in->ModeCount, ctx->targetId);
		}

		out->AdapterLuid = ctx->adapterLuid;
		out->TargetId = ctx->targetId;
		bytesReturned = sizeof(*out);
		break;
	}
	case IOCTL_EXO_REMOVE_MONITOR: {
		ExoRemoveMonitor* in;
		Status = WdfRequestRetrieveInputBuffer(Request, sizeof(*in), (PVOID*)&in, NULL);
		if (!NT_SUCCESS(Status)) {
			break;
		}
		if (!FindMonitor(in->Id)) {
			Status = STATUS_NOT_FOUND;
			break;
		}
		ExoLog("unplug requested");
		Unplug(&in->Id);
		break;
	}
	case IOCTL_EXO_SET_RENDER_ADAPTER: {
		ExoSetRenderAdapter* in;
		Status = WdfRequestRetrieveInputBuffer(Request, sizeof(*in), (PVOID*)&in, NULL);
		if (!NT_SUCCESS(Status)) {
			break;
		}
		preferredAdapterLuid = in->AdapterLuid;
		pDevice->SetRenderAdapter(in->AdapterLuid);
		preferredAdapterChanged = true;
		break;
	}
	case IOCTL_EXO_PING:
		Status = STATUS_SUCCESS;
		break;
	case IOCTL_EXO_GET_FRAME: {
		ExoFrameInfo* out;
		Status = WdfRequestRetrieveOutputBuffer(Request, sizeof(*out), (PVOID*)&out, NULL);
		if (!NT_SUCCESS(Status)) {
			break;
		}
		{
			std::lock_guard<std::mutex> lg(frameLock);
			*out = frameInfo;
		}
		bytesReturned = sizeof(*out);
		break;
	}
	case IOCTL_EXO_GET_CURSOR: {
		// METHOD_BUFFERED: input and output share one buffer, so the query
		// is read before anything is written.
		ExoCursorQuery* in;
		BYTE* out;
		size_t outLength;
		Status = WdfRequestRetrieveInputBuffer(Request, sizeof(*in), (PVOID*)&in, NULL);
		if (!NT_SUCCESS(Status)) {
			break;
		}
		UINT32 have = in->HaveShapeId;
		Status = WdfRequestRetrieveOutputBuffer(Request, sizeof(ExoCursorInfo), (PVOID*)&out, &outLength);
		if (!NT_SUCCESS(Status)) {
			break;
		}
		std::lock_guard<std::mutex> lg(cursorLock);
		ExoCursorInfo info = cursorInfo;
		info.ShapeBytes = 0;
		if (info.ShapeId != have && !cursorShape.empty() && outLength >= sizeof(info) + cursorShape.size()) {
			info.ShapeBytes = (UINT32)cursorShape.size();
			memcpy(out + sizeof(info), cursorShape.data(), cursorShape.size());
		}
		memcpy(out, &info, sizeof(info));
		bytesReturned = sizeof(info) + info.ShapeBytes;
		break;
	}
	case IOCTL_EXO_GET_VERSION: {
		ExoVersionOut* out;
		Status = WdfRequestRetrieveOutputBuffer(Request, sizeof(*out), (PVOID*)&out, NULL);
		if (!NT_SUCCESS(Status)) {
			break;
		}
		out->Version = ExoDisplayVersion;
		bytesReturned = sizeof(*out);
		break;
	}
	default:
		break;
	}

	WdfRequestCompleteWithInformation(Request, Status, bytesReturned);
}
