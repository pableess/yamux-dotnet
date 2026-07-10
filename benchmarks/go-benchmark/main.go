package main

import (
	"fmt"
	"io"
	"net"
	"os"
	"time"

	"github.com/hashicorp/yamux"
)

const dataChunkSize = 32 * 1024 // 32KB, matching C# benchmark

type result struct {
	Label    string
	MBs      int
	Streams  int
	Duration time.Duration
	MBps     float64
}

func main() {
	mbsValues := []int{1, 50, 500}
	streamsValues := []int{1, 5, 20}

	results := []result{}

	for _, mbs := range mbsValues {
		for _, streams := range streamsValues {
			d, err := runRawTCP(mbs, streams)
			if err == nil {
				results = append(results, d)
			}

			d, err = runGoYamux(mbs, streams)
			if err == nil {
				results = append(results, d)
			}
		}
	}

	// Print results table
	fmt.Printf("\n%-20s %5s %8s %12s %10s\n", "Method", "MBs", "Streams", "Duration", "MB/s")
	fmt.Println("-------------------------------------------------------------")
	for _, r := range results {
		fmt.Printf("%-20s %5d %8d %12v %10.1f\n", r.Label, r.MBs, r.Streams, r.Duration.Round(time.Millisecond), r.MBps)
	}
}

func runRawTCP(mbs, streams int) (result, error) {
	totalMB := mbs * streams

	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return result{}, err
	}
	port := listener.Addr().(*net.TCPAddr).Port

	serverErr := make(chan error, 1)
	go func() {
		conn, err := listener.Accept()
		if err != nil {
			serverErr <- err
			return
		}
		defer conn.Close()
		listener.Close()

		totalBytes := totalMB * 1024 * 1024
		buf := make([]byte, dataChunkSize)
		written := 0
		for written < totalBytes {
			n := len(buf)
			if remaining := totalBytes - written; remaining < n {
				n = remaining
			}
			if _, err := conn.Write(buf[:n]); err != nil {
				serverErr <- err
				return
			}
			written += n
		}
		serverErr <- nil
	}()

	conn, err := net.Dial("tcp", fmt.Sprintf("127.0.0.1:%d", port))
	if err != nil {
		return result{}, err
	}

	start := time.Now()
	totalBytes := totalMB * 1024 * 1024
	buf := make([]byte, dataChunkSize)
	read := 0
	for read < totalBytes {
		n, err := conn.Read(buf)
		if err != nil {
			conn.Close()
			return result{}, err
		}
		read += n
	}
	elapsed := time.Since(start)
	conn.Close()

	if err := <-serverErr; err != nil {
		return result{}, err
	}

	mbps := float64(totalMB) / elapsed.Seconds()
	return result{
		Label:    "GoRawTCP",
		MBs:      mbs,
		Streams:  streams,
		Duration: elapsed,
		MBps:     mbps,
	}, nil
}

func runGoYamux(mbs, streams int) (result, error) {
	totalMB := mbs * streams

	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return result{}, err
	}
	port := listener.Addr().(*net.TCPAddr).Port

	serverErr := make(chan error, 1)
	go func() {
		conn, err := listener.Accept()
		if err != nil {
			serverErr <- err
			return
		}
		listener.Close()

		config := yamux.DefaultConfig()
		config.LogOutput = io.Discard
		config.EnableKeepAlive = false
		session, err := yamux.Server(conn, config)
		if err != nil {
			serverErr <- err
			return
		}

		// Accept streams and drain them
		done := make(chan struct{}, streams)
		for i := 0; i < streams; i++ {
			stream, err := session.AcceptStream()
			if err != nil {
				serverErr <- err
				return
			}
			go func(s *yamux.Stream) {
				io.CopyBuffer(io.Discard, s, make([]byte, dataChunkSize))
				s.Close()
				done <- struct{}{}
			}(stream)
		}

		// Wait for all streams to finish
		for i := 0; i < streams; i++ {
			<-done
		}
		serverErr <- nil
	}()

	conn, err := net.Dial("tcp", fmt.Sprintf("127.0.0.1:%d", port))
	if err != nil {
		return result{}, err
	}

	config := yamux.DefaultConfig()
	config.LogOutput = io.Discard
	config.EnableKeepAlive = false
	session, err := yamux.Client(conn, config)
	if err != nil {
		return result{}, err
	}

	iterationsPerStream := (totalMB * 1024 * 1024 / dataChunkSize) / streams
	buf := make([]byte, dataChunkSize)

	start := time.Now()
	errCh := make(chan error, streams)

	for i := 0; i < streams; i++ {
		go func() {
			stream, err := session.OpenStream()
			if err != nil {
				errCh <- err
				return
			}
			for j := 0; j < iterationsPerStream; j++ {
				if _, err := stream.Write(buf); err != nil {
					errCh <- err
					return
				}
			}
			stream.Close()
			errCh <- nil
		}()
	}

	for i := 0; i < streams; i++ {
		if err := <-errCh; err != nil {
			return result{}, err
		}
	}
	elapsed := time.Since(start)

	conn.Close()
	session.Close()

	if err := <-serverErr; err != nil {
		return result{}, err
	}

	mbps := float64(totalMB) / elapsed.Seconds()
	return result{
		Label:    "GoYamux",
		MBs:      mbs,
		Streams:  streams,
		Duration: elapsed,
		MBps:     mbps,
	}, nil
}

func init() {
	// Ensure we don't get "too many open files" errors
	// by keeping the listener port free
	os.Setenv("GODEBUG", os.Getenv("GODEBUG")+",netdns=go")
}