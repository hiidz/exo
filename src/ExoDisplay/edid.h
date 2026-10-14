// The monitor's EDID: one 128-byte block with no extension, so Windows sees
// no HDR or audio claims. Manufacturer EXO, which is not in the PNP ID
// registry, and product 0001, so Windows names the monitor EXO0001. The hub
// supplies the serial number and strings from the phone and its mode, so
// each phone at each size is its own monitor to Windows and keeps its own
// position and scale.
// Monitor modes come from ExoAddMonitor, not from the timings here.

#pragma once

#include <string.h>

#define EDID_OFFSET_SERIAL 12
#define EDID_OFFSET_SIZE_CM 21
#define EDID_OFFSET_DTD 54
#define EDID_OFFSET_SERIALSTR 77
#define EDID_OFFSET_PRODNAME 113

static const BYTE edid_base[128] = {
	0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x00, // header
	0x17, 0x0f,                                     // manufacturer EXO
	0x01, 0x00,                                     // product 0001
	0x00, 0x00, 0x00, 0x00,                         // serial number, from Id
	0x2a, 0x24,                                     // week 42, 2026
	0x01, 0x03,                                     // EDID 1.3
	0x80,                                           // digital input
	0x00, 0x00,                                     // size in cm, from the hub
	0x78,                                           // gamma 2.2
	0x06,                                           // sRGB, preferred timing first
	0xee, 0x91, 0xa3, 0x54, 0x4c, 0x99, 0x26, 0x0f, 0x50, 0x54, // sRGB primaries, D65
	0x00, 0x00, 0x00,                               // no established timings
	0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, // no standard timings
	0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
	// Detailed timing, 3840x2160 at 60, from SudoVDA. Only its size in mm is set.
	0x4d, 0xd0, 0x00, 0xa0, 0xf0, 0x70, 0x3e, 0x80, 0x30, 0x20, 0x35, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1a,
	// Serial string, from the hub
	0x00, 0x00, 0x00, 0xff, 0x00, 0x0a, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20,
	// Range limits: 15 to 255Hz, 20 to 255kHz, 2550MHz
	0x00, 0x00, 0x00, 0xfd, 0x00, 0x0f, 0xff, 0x14, 0xff, 0xff, 0x00, 0x0a, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20,
	// Name, from the hub
	0x00, 0x00, 0x00, 0xfc, 0x00, 0x45, 0x78, 0x6f, 0x0a, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20,
	0x00,                                           // no extension blocks
	0x00,                                           // checksum
};

// A 13-byte descriptor string: up to 13 characters, then a line feed, then
// spaces. An empty s leaves the base's text.
static void WriteEdidString(BYTE* field, const char* s, size_t max)
{
	size_t n = strnlen(s, max);
	if (n == 0) {
		return;
	}
	if (n > 13) {
		n = 13;
	}
	memcpy(field, s, n);
	if (n < 13) {
		field[n] = 0x0a;
		memset(field + n + 1, ' ', 13 - n - 1);
	}
}

static void MakeEdid(BYTE edid[128], const ExoAddMonitor& p)
{
	memcpy(edid, edid_base, 128);
	memcpy(edid + EDID_OFFSET_SERIAL, &p.Id.Data1, 4);

	if (p.WidthMm && p.HeightMm) {
		UINT w = p.WidthMm > 4095 ? 4095 : p.WidthMm;
		UINT h = p.HeightMm > 4095 ? 4095 : p.HeightMm;
		edid[EDID_OFFSET_SIZE_CM] = (BYTE)((w + 5) / 10 > 255 ? 255 : (w + 5) / 10);
		edid[EDID_OFFSET_SIZE_CM + 1] = (BYTE)((h + 5) / 10 > 255 ? 255 : (h + 5) / 10);
		edid[EDID_OFFSET_DTD + 12] = (BYTE)(w & 0xff);
		edid[EDID_OFFSET_DTD + 13] = (BYTE)(h & 0xff);
		edid[EDID_OFFSET_DTD + 14] = (BYTE)(((w >> 8) << 4) | (h >> 8));
	}

	WriteEdidString(edid + EDID_OFFSET_SERIALSTR, p.Serial, sizeof(p.Serial));
	WriteEdidString(edid + EDID_OFFSET_PRODNAME, p.Name, sizeof(p.Name));

	BYTE sum = 0;
	for (int i = 0; i < 127; i++) {
		sum += edid[i];
	}
	edid[127] = (BYTE)(256 - sum);
}
