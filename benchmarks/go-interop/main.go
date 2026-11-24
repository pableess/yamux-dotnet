// Command go-interop is a small yamux peer used by the .NET interoperability
// tests. It can act either as a Go server or as a Go client and exercises
// stream open/accept, bidirectional data, FIN and GoAway handling.
package main

import (
	"bytes"
	"flag"
	"fmt"
	"io"
	"net"
	"os"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/hashicorp/yamux"
)

var (
	mode         = flag.String("mode", "server-echo", "server-echo|server-drain|server-send|server-send-close|server-ping|server-goaway|client-echo|client-send|client-recv|client-ping")
	streams      = flag.Int("streams", 1, "number of yamux streams")
	totalMB      = flag.Int("mb", 1, "total megabytes")
	chunkSize    = flag.Int("chunk", 32*1024, "write/read chunk size")
	window       = flag.Uint("window", 0, "MaxStreamWindowSize (0 keeps the Go default)")
	enableKA     = flag.Bool("keepalive", false, "enable yamux keep-alive")
	port         = flag.Int("port", 0, "listen port for server modes")
	addr         = flag.String("addr", "", "server address for client modes")
	closeTimeout = flag.Duration("close-timeout", 0, "StreamCloseTimeout (used to trigger RST)")
)

func newConfig() *yamux.Config {
	cfg := yamux.DefaultConfig()
	cfg.LogOutput = os.Stderr
	cfg.EnableKeepAlive = *enableKA
	if *window > 0 {
		cfg.MaxStreamWindowSize = uint32(*window)
	}
	if *closeTimeout > 0 {
		cfg.StreamCloseTimeout = *closeTimeout
	}
	return cfg
}

func perStreamBytes() int {
	total := *totalMB * 1024 * 1024
	if *streams <= 0 {
		return total
	}
	return total / *streams
}

func minInt(a, b int) int {
	if a < b {
		return a
	}
	return b
}

// pattern fills buf with a deterministic sequence so the peer can verify it.
func pattern(seed, offset int, buf []byte) {
	for i := range buf {
		buf[i] = byte((seed*31 + offset + i) % 251)
	}
}

func main() {
	flag.Parse()

	var err error
	switch {
	case strings.HasPrefix(*mode, "server-"):
		err = runServer()
	case strings.HasPrefix(*mode, "client-"):
		err = runClient()
	default:
		err = fmt.Errorf("unknown mode %q", *mode)
	}

	if err != nil {
		fmt.Fprintf(os.Stderr, "ERROR: %v\n", err)
		os.Exit(1)
	}
}

func runServer() error {
	ln, err := net.Listen("tcp", fmt.Sprintf("127.0.0.1:%d", *port))
	if err != nil {
		return err
	}
	defer ln.Close()

	fmt.Printf("LISTENING:%d\n", ln.Addr().(*net.TCPAddr).Port)
	os.Stdout.Sync()

	conn, err := ln.Accept()
	if err != nil {
		return err
	}
	_ = ln.Close()

	session, err := yamux.Server(conn, newConfig())
	if err != nil {
		return err
	}
	defer session.Close()

	switch *mode {
	case "server-echo":
		return serveEcho(session)
	case "server-drain":
		return serveDrain(session)
	case "server-send":
		return serveSend(session, false)
	case "server-send-close":
		return serveSend(session, true)
	case "server-ping":
		<-session.CloseChan()
		return nil
	case "server-goaway":
		return serveGoAway(session)
	default:
		return fmt.Errorf("unknown server mode %q", *mode)
	}
}

func serveEcho(session *yamux.Session) error {
	var wg sync.WaitGroup
	for {
		stream, err := session.AcceptStream()
		if err != nil {
			break
		}

		wg.Add(1)
		go func(s *yamux.Stream) {
			defer wg.Done()
			_, _ = io.Copy(s, s)
			_ = s.Close()
		}(stream)
	}
	wg.Wait()
	return nil
}

func serveDrain(session *yamux.Session) error {
	var wg sync.WaitGroup
	for {
		stream, err := session.AcceptStream()
		if err != nil {
			break
		}

		wg.Add(1)
		go func(s *yamux.Stream) {
			defer wg.Done()
			_, _ = io.Copy(io.Discard, s)
			_ = s.Close()
		}(stream)
	}
	wg.Wait()
	return nil
}

func serveSend(session *yamux.Session, holdUntilReset bool) error {
	stream, err := session.AcceptStream()
	if err != nil {
		return err
	}

	n := perStreamBytes()
	buf := make([]byte, *chunkSize)
	sent := 0
	for sent < n {
		m := minInt(len(buf), n-sent)
		pattern(1, sent, buf[:m])
		if _, err := stream.Write(buf[:m]); err != nil {
			return err
		}
		sent += m
	}

	if err := stream.Close(); err != nil {
		return err
	}

	fmt.Printf("DONE:%d\n", sent)
	os.Stdout.Sync()

	if holdUntilReset {
		// Wait for StreamCloseTimeout to fire and send a RST, since the peer
		// deliberately does not respond with FIN.
		time.Sleep(*closeTimeout + 500*time.Millisecond)
		return nil
	}

	// Wait for the peer's FIN so the data has been fully consumed.
	_, _ = io.Copy(io.Discard, stream)
	return nil
}

func serveGoAway(session *yamux.Session) error {
	if err := session.GoAway(); err != nil {
		return err
	}

	fmt.Printf("DONE:0\n")
	os.Stdout.Sync()

	<-session.CloseChan()
	return nil
}

func runClient() error {
	if *addr == "" {
		return fmt.Errorf("client mode requires -addr")
	}

	conn, err := net.Dial("tcp", *addr)
	if err != nil {
		return err
	}
	defer conn.Close()

	session, err := yamux.Client(conn, newConfig())
	if err != nil {
		return err
	}
	defer session.Close()

	switch *mode {
	case "client-echo":
		return clientEcho(session)
	case "client-send":
		return clientSend(session)
	case "client-recv":
		return clientRecv(session)
	case "client-ping":
		return clientPing(session)
	default:
		return fmt.Errorf("unknown client mode %q", *mode)
	}
}

func clientEcho(session *yamux.Session) error {
	n := perStreamBytes()

	var wg sync.WaitGroup
	var total int64
	var firstErr error
	var errOnce sync.Once

	fail := func(err error) {
		errOnce.Do(func() { firstErr = err })
	}

	for i := 0; i < *streams; i++ {
		wg.Add(1)
		go func(seed int) {
			defer wg.Done()

			stream, err := session.OpenStream()
			if err != nil {
				fail(err)
				return
			}
			defer stream.Close()

			buf := make([]byte, *chunkSize)
			sent := 0
			writeDone := make(chan error, 1)

			// Read concurrently with writing: the echo server applies backpressure
			// and would deadlock with a write-everything-then-read client.
			go func() {
				for sent < n {
					m := minInt(len(buf), n-sent)
					pattern(seed, sent, buf[:m])
					if _, err := stream.Write(buf[:m]); err != nil {
						writeDone <- err
						return
					}
					sent += m
				}

				// Close the write side; reads remain valid until the echo FIN.
				writeDone <- stream.Close()
			}()

			received := 0
			expected := make([]byte, *chunkSize)
			readBuf := make([]byte, *chunkSize)
			for received < n {
				m := minInt(len(readBuf), n-received)
				if _, err := io.ReadFull(stream, readBuf[:m]); err != nil {
					_ = stream.Close()
					fail(fmt.Errorf("stream %d read at %d: %w", seed, received, err))
					return
				}

				pattern(seed, received, expected[:m])
				if !bytes.Equal(readBuf[:m], expected[:m]) {
					_ = stream.Close()
					fail(fmt.Errorf("stream %d data mismatch at %d", seed, received))
					return
				}
				received += m
			}

			if err := <-writeDone; err != nil {
				fail(err)
				return
			}

			atomic.AddInt64(&total, int64(n))
		}(i)
	}

	wg.Wait()
	if firstErr != nil {
		return firstErr
	}

	fmt.Printf("DONE:%d\n", atomic.LoadInt64(&total))
	os.Stdout.Sync()
	return nil
}

func clientSend(session *yamux.Session) error {
	n := perStreamBytes()

	var wg sync.WaitGroup
	var total int64
	var firstErr error
	var errOnce sync.Once

	for i := 0; i < *streams; i++ {
		wg.Add(1)
		go func(seed int) {
			defer wg.Done()

			stream, err := session.OpenStream()
			if err != nil {
				errOnce.Do(func() { firstErr = err })
				return
			}
			defer stream.Close()

			buf := make([]byte, *chunkSize)
			sent := 0
			for sent < n {
				m := minInt(len(buf), n-sent)
				pattern(seed, sent, buf[:m])
				if _, err := stream.Write(buf[:m]); err != nil {
					errOnce.Do(func() { firstErr = err })
					return
				}
				sent += m
			}

			if err := stream.Close(); err != nil {
				errOnce.Do(func() { firstErr = err })
				return
			}

			atomic.AddInt64(&total, int64(n))
		}(i)
	}

	wg.Wait()
	if firstErr != nil {
		return firstErr
	}

	fmt.Printf("DONE:%d\n", atomic.LoadInt64(&total))
	os.Stdout.Sync()
	return nil
}

func clientRecv(session *yamux.Session) error {
	stream, err := session.AcceptStream()
	if err != nil {
		return err
	}
	defer stream.Close()

	n, err := io.Copy(io.Discard, stream)
	if err != nil {
		return err
	}

	fmt.Printf("RECEIVED:%d\n", n)
	os.Stdout.Sync()
	return nil
}

func clientPing(session *yamux.Session) error {
	rtt, err := session.Ping()
	if err != nil {
		return err
	}

	fmt.Printf("RTT_MS:%.3f\n", float64(rtt)/float64(time.Millisecond))
	os.Stdout.Sync()
	return nil
}
