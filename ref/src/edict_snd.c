/*
Copyright (C) 2026 Andre Kaufmann
SPDX-License-Identifier: GPL-2.0-or-later

The sound build's DMA driver (build.sh SOUND=1, docs/PLAN.md P6 step 8): snd_dma.c and snd_mix.c paint into a
buffer of 32768 16-bit samples, stereo, 11025 Hz (snd_dma.c's desired speed and bits), and the "DMA position" the
mixer paints ahead of is the host's realtime in samples: deterministic, a function of the frame clock only. After
every host frame the harness hashes the buffer with the painted and the sound time ('A' record, edict_hash.c).
*/

#include "quakedef.h"
#include "edict_hash.h"

#define EDICT_SND_SAMPLES	32768

static unsigned char	edict_snd_buffer[EDICT_SND_SAMPLES * 2];

extern volatile dma_t	sn;
extern int				paintedtime, soundtime, sound_started;

qboolean SNDDMA_Init (void)
{
	shm = &sn;
	shm->splitbuffer = 0;
	shm->samplebits = 16;
	shm->speed = 11025;
	shm->channels = 2;
	shm->samples = EDICT_SND_SAMPLES;
	shm->samplepos = 0;
	shm->soundalive = true;
	shm->gamealive = true;
	shm->submission_chunk = 1;
	shm->buffer = edict_snd_buffer;
	return true;
}

/* the samples (both channels) the "hardware" has played: the host's realtime, wrapped in the buffer */
int SNDDMA_GetDMAPos (void)
{
	shm->samplepos = ((int)(realtime * shm->speed) * shm->channels) & (shm->samples - 1);
	return shm->samplepos;
}

void SNDDMA_Shutdown (void)
{
}

void SNDDMA_Submit (void)
{
}

/* after a host frame: the buffer, paintedtime and soundtime into the hash */
void EdictSnd_Frame (void)
{
	if (!sound_started || !shm)
		return;
	EdictHash_Sound (edict_snd_buffer, sizeof (edict_snd_buffer), paintedtime, soundtime);
}
