// Hourly keep-alive: inserts a heartbeat row into public.polling and reads
// back the current rows. Purpose is solely to keep the Supabase project's
// API active so its free-tier auto-pause never triggers -- rows themselves
// are disposable and get wiped daily by a pg_cron job in the database.
package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"
)

const defaultNtfyURL = "https://ntfy.sh/simpleboardalerts"

var client = &http.Client{Timeout: 30 * time.Second}

func main() {
	supabaseURL := strings.TrimRight(mustEnv("SUPABASE_URL"), "/")
	key := mustEnv("SUPABASE_PUBLISHABLE_KEY")
	ntfyURL := os.Getenv("NTFY_URL")
	if ntfyURL == "" {
		ntfyURL = defaultNtfyURL
	}

	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()

	run := func() {
		if err := poll(ctx, supabaseURL, key); err != nil {
			log.Printf("Polling heartbeat failed: %v", err)
			notify(ctx, ntfyURL, "SimpleBoard polling heartbeat failed: "+err.Error())
		}
	}

	run() // fire once at startup so a bad config surfaces immediately
	for {
		next := time.Now().Truncate(time.Hour).Add(time.Hour)
		select {
		case <-ctx.Done():
			return
		case <-time.After(time.Until(next)):
			run()
		}
	}
}

func mustEnv(name string) string {
	v := os.Getenv(name)
	if v == "" {
		log.Fatalf("%s is not configured", name)
	}
	return v
}

func poll(ctx context.Context, baseURL, key string) error {
	do := func(method, path string, body io.Reader) ([]byte, error) {
		req, err := http.NewRequestWithContext(ctx, method, baseURL+"/"+path, body)
		if err != nil {
			return nil, err
		}
		req.Header.Set("apikey", key)
		req.Header.Set("Authorization", "Bearer "+key)
		if body != nil {
			req.Header.Set("Content-Type", "application/json")
		}
		resp, err := client.Do(req)
		if err != nil {
			return nil, err
		}
		defer resp.Body.Close()
		data, _ := io.ReadAll(resp.Body)
		if resp.StatusCode < 200 || resp.StatusCode > 299 {
			return nil, fmt.Errorf("%s %s: %s", method, path, resp.Status)
		}
		return data, nil
	}

	if _, err := do(http.MethodPost, "rest/v1/polling", bytes.NewReader([]byte("{}"))); err != nil {
		return err
	}
	data, err := do(http.MethodGet, "rest/v1/polling?select=*&order=polled_at.desc", nil)
	if err != nil {
		return err
	}
	var rows []json.RawMessage
	if err := json.Unmarshal(data, &rows); err != nil {
		return fmt.Errorf("decode rows: %w", err)
	}
	log.Printf("Polling heartbeat sent. Current row count: %d", len(rows))
	return nil
}

func notify(ctx context.Context, url, msg string) {
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, url, strings.NewReader(msg))
	if err != nil {
		return
	}
	resp, err := client.Do(req)
	if err != nil {
		log.Printf("Failed to publish ntfy.sh alert: %v", err)
		return
	}
	resp.Body.Close()
}
