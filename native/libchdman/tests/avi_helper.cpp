// license:BSD-3-Clause
// Test helper: writes a small deterministic YUY2 + PCM AVI using MAME's aviio.

#include "aviio.h"
#include "bitmap.h"

#include <cstdint>
#include <vector>

extern "C" int chdman_test_write_avi(const char *path, int width, int height, int frames, int fps, int rate, int channels)
{
	avi_file::movie_info info;
	info.video_format = avi_file::FORMAT_YUY2;
	info.video_timescale = fps * 1000000;
	info.video_sampletime = 1000000;
	info.video_numsamples = 0;
	info.video_width = width;
	info.video_height = height;
	info.video_depth = 16;
	info.audio_format = 0;
	info.audio_timescale = rate;
	info.audio_sampletime = 1;
	info.audio_numsamples = 0;
	info.audio_channels = channels;
	info.audio_samplebits = 16;
	info.audio_samplerate = rate;

	avi_file::ptr file;
	if (avi_file::create(path, info, file) != avi_file::error::NONE)
		return 0;

	bitmap_yuy16 bitmap(width, height);
	uint32_t seed = 12345;
	int const samples_per_frame = rate / fps;
	std::vector<int16_t> audio(samples_per_frame);
	for (int frame = 0; frame < frames; frame++)
	{
		for (int y = 0; y < height; y++)
			for (int x = 0; x < width; x++)
			{
				// smooth gradient plus some noise: compressible but not trivial
				seed = seed * 1103515245 + 12345;
				uint8_t const luma = uint8_t((x * 2 + y + frame * 3) + ((seed >> 16) & 7));
				uint8_t const chroma = uint8_t(128 + ((x / 8 + frame) & 0x1f));
				bitmap.pix(y, x) = uint16_t((luma << 8) | chroma);
			}
		for (int ch = 0; ch < channels; ch++)
		{
			for (int s = 0; s < samples_per_frame; s++)
				audio[s] = int16_t(((frame * samples_per_frame + s) * (ch + 3) * 97) & 0x3fff) - 0x2000;
			if (file->append_sound_samples(ch, audio.data(), samples_per_frame, 0) != avi_file::error::NONE)
				return 0;
		}
		if (file->append_video_frame(bitmap) != avi_file::error::NONE)
			return 0;
	}
	file.reset();
	return 1;
}
