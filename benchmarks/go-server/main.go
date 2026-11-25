package main

import (
	"fmt"
	"io"
	"net"
	"os"
	"sync"

	"github.com/hashicorp/yamux"
)

const dataChunkSize = 32 * 1024

// The server stays alive across connections so the benchmark can exclude
// process startup and JIT from the measured method.
func main() {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		fmt.Fprintf(os.Stderr, "listen error: %v\n", err)
		os.Exit(1)
	}

	port := listener.Addr().(*net.TCPAddr).Port
	fmt.Printf("LISTENING:%d\n", port)
	os.Stdout.Sync()

	for {
		conn, err := listener.Accept()
		if err != nil {
			fmt.Fprintf(os.Stderr, "accept error: %v\n", err)
			os.Exit(1)
		}

		if err := serve(conn); err != nil {
			fmt.Fprintf(os.Stderr, "connection error: %v\n", err)
		}
	}
}

func serve(conn net.Conn) error {
	config := yamux.DefaultConfig()
	config.LogOutput = io.Discard
	config.EnableKeepAlive = false
	session, err := yamux.Server(conn, config)
	if err != nil {
		conn.Close()
		return err
	}
	defer conn.Close()

	var drains sync.WaitGroup
	for {
		stream, err := session.AcceptStream()
		if err != nil {
			break
		}

		drains.Add(1)
		go func(s *yamux.Stream) {
			defer drains.Done()
			_, _ = io.CopyBuffer(io.Discard, s, make([]byte, dataChunkSize))
			s.Close()
		}(stream)
	}

	drains.Wait()
	session.Close()
	return nil
}
