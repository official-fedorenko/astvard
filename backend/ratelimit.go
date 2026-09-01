package main

import (
	"sync"
	"time"
)

type rateLimitEntry struct {
	count       int
	windowStart time.Time
}

// В Node каждый запрос обрабатывался по очереди в одном потоке, поэтому обычная Map была безопасна.
// В Go net/http обрабатывает каждый запрос в отдельной горутине параллельно — если два запроса
// одновременно полезут в одну map, программа может упасть. Mutex — это "замок": только одна
// горутина одновременно может читать/писать карту.
type rateLimiter struct {
	mu      sync.Mutex
	entries map[string]*rateLimitEntry
}

func newRateLimiter() *rateLimiter {
	return &rateLimiter{entries: make(map[string]*rateLimitEntry)}
}

func (rl *rateLimiter) isLimited(key string, limit int, window time.Duration) bool {
	rl.mu.Lock()
	defer rl.mu.Unlock()

	now := time.Now()
	entry, exists := rl.entries[key]

	if !exists || now.Sub(entry.windowStart) > window {
		rl.entries[key] = &rateLimitEntry{count: 1, windowStart: now}
		return false
	}

	entry.count++
	return entry.count > limit
}
