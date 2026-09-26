package indefinite

import (
	"context"
	"os"
	"regexp"
	"strings"
	"testing"
)

// The README is a contract: its examples are real.

func readme(t *testing.T) string {
	t.Helper()
	b, err := os.ReadFile("../README.md")
	if err != nil {
		t.Fatal(err)
	}
	return string(b)
}

func TestTheReadmeFaultLineIsReal(t *testing.T) {
	// It is what seed 13 writes on the fifth call to app.get -- as in Python.
	get := NewSite("app.get")
	log := &syncBuffer{}
	ctx, in := inject(context.Background(), 13, log)
	defer in.close()
	for range 4 {
		if _, faulted := catch(func() { _ = get.Do(ctx, func() error { return nil }) }); faulted {
			t.Fatal("an earlier call faulted")
		}
	}
	if _, faulted := catch(func() { _ = get.Do(ctx, func() error { return nil }) }); !faulted {
		t.Fatal("the fifth call didn't fault")
	}
	shown := regexp.MustCompile("```text\n(indefinite-error: .*\n)```").FindStringSubmatch(readme(t))
	if shown == nil || shown[1] != log.String() {
		t.Fatalf("README shows %q; seed 13 writes %q", shown, log.String())
	}
}

func TestTheReadmeTryItIsTheExample(t *testing.T) {
	// The block and its output are Example's, which go test runs.
	src, err := os.ReadFile("example_test.go")
	if err != nil {
		t.Fatal(err)
	}
	example := regexp.MustCompile(`(?s)func Example\(\) \{\n(.*?)\t// Output:\n(.*?)\n\}`).FindStringSubmatch(string(src))
	if example == nil {
		t.Fatal("no Example() with an // Output: in example_test.go")
	}
	body := strings.ReplaceAll(example[1], "\n\t", "\n")
	body = strings.TrimPrefix(body, "\t")
	body = regexp.MustCompile(` // .*`).ReplaceAllString(body, "")
	output := strings.ReplaceAll(example[2], "\t// ", "")
	doc := readme(t)
	if !strings.Contains(doc, "```go\n"+strings.TrimSpace(body)+"\n```") {
		t.Errorf("README's Try it block differs from Example's body:\n%s", body)
	}
	if !strings.Contains(doc, output) {
		t.Errorf("README's Try it output differs from Example's:\n%s", output)
	}
}
