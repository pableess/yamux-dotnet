package main

import (
	"fmt"
	"io"
	"net"
	"sync"
	"time"

	"github.com/hashicorp/yamux"
)

const dataChunkSize = 32 * 1024 // 32KB application writes, matching the C# harness

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

	fmt.Printf("\n%-20s %5s %8s %12s %10s\n", "Method", "MBs", "Streams", "Duration", "MB/s")
	fmt.Println("-------------------------------------------------------------")
	for _, r := range results {
		fmt.Printf("%-20s %5d %8d %12v %10.1f\n", r.Label, r.MBs, r.Streams, r.Duration.Round(time.Millisecond), r.MBps)
	}
}

// chunksForStream distributes totalChunks across streams as evenly as possible.
func chunksForStream(totalChunks, streams, index int) int {
	n := totalChunks / streams
	if index < totalChunks%streams {
		n++
	}
	return n
}

// runRawTCP measures one TCP byte stream carrying the same total payload as
// the multiplexed benchmarks. Raw TCP has no streams, so the streams parameter
// only scales the reported total.
func runRawTCP(mbs, streams int) (result, error) {
	totalBytes := mbs * 1024 * 1024

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

	// Match the C# harness, where the connection is part of the measured method.
	start := time.Now()
	conn, err := net.Dial("tcp", fmt.Sprintf("127.0.0.1:%d", port))
	if err != nil {
		return result{}, err
	}

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

	mbps := float64(mbs) / elapsed.Seconds()
	return result{
		Label:    "GoRawTCP",
		MBs:      mbs,
		Streams:  streams,
		Duration: elapsed,
		MBps:     mbps,
	}, nil
}

func runGoYamux(mbs, streams int) (result, error) {
	totalChunks := mbs * 1024 * 1024 / dataChunkSize

	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return result{}, err
	}
	port := listener.Addr().(*net.TCPAddr).Port

	serverErr := make(chan error, 1)
	streamDrained := make(chan struct{}, streams)

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
			conn.Close()
			serverErr <- err
			return
		}

		var drains sync.WaitGroup
		acceptErr := error(nil)
		for i := 0; i < streams; i++ {
			stream, err := session.AcceptStream()
			if err != nil {
				acceptErr = err
				break
			}

			drains.Add(1)
			go func(s *yamux.Stream) {
				defer drains.Done()
				_, _ = io.CopyBuffer(io.Discard, s, make([]byte, dataChunkSize))
				s.Close()
				streamDrained <- struct{}{}
			}(stream)
		}

		drains.Wait()
		session.Close()
		conn.Close()
		serverErr <- acceptErr
	}()

	// Match the C# harness: dial + session creation are part of the measurement.
	start := time.Now()
	conn, err := net.Dial("tcp", fmt.Sprintf("127.0.0.1:%d", port))
	if err != nil {
		return result{}, err
	}

	config := yamux.DefaultConfig()
	config.LogOutput = io.Discard
	config.EnableKeepAlive = false
	session, err := yamux.Client(conn, config)
	if err != nil {
		conn.Close()
		return result{}, err
	}

	buf := make([]byte, dataChunkSize)
	errCh := make(chan error, streams)

	for i := 0; i < streams; i++ {
		streamIndex := i
		go func() {
			stream, err := session.OpenStream()
			if err != nil {
				errCh <- err
				return
			}

			for j := 0; j < chunksForStream(totalChunks, streams, streamIndex); j++ {
				if _, err := stream.Write(buf); err != nil {
					errCh <- err
					return
				}
			}

			if err := stream.Close(); err != nil {
				errCh <- err
				return
			}

			errCh <- nil
		}()
	}

	for i := 0; i < streams; i++ {
		if err := <-errCh; err != nil {
			conn.Close()
			session.Close()
			return result{}, err
		}
	}

	// Wait until the receiver has drained every stream so the measurement is
	// end-to-end rather than "bytes handed to the local send buffer".
	for i := 0; i < streams; i++ {
		<-streamDrained
	}
	elapsed := time.Since(start)

	conn.Close()
	session.Close()

	if err := <-serverErr; err != nil && err != yamux.ErrSessionShutdown {
		return result{}, err
	}

	mbps := float64(mbs) / elapsed.Seconds()
	return result{
		Label:    "GoYamux",
		MBs:      mbs,
		Streams:  streams,
		Duration: elapsed,
		MBps:     mbps,
	}, nil
}
